#!/usr/bin/env node
// Launcher for the code-index-mcp stdio server.
//
// Unlike a pure-native plugin, this one has real prerequisites beyond "a .NET
// runtime is on PATH": Ollama must be running, the configured embedding model
// must actually be pulled (~2.5 GB), and at least one project must be
// configured — none of which has a sensible default, since project paths are
// only known to the user. A person who just installed this plugin and asks a
// question should see one of the messages below, not a raw stack trace or a
// silent hang. So every check runs here, before the .NET process is even
// spawned, and prints to stderr — stdout is reserved for the MCP protocol
// stream once the child process starts.
//
// Layout:
//   bin/server.js          <- this file
//   bin/server/CodeIndex.Server.dll (+ deps, appsettings.json)
//
// The server is a portable, framework-dependent build (no native/AOT
// dependencies anywhere in the dependency graph — see CodeIndex.Core.csproj),
// so a single publish output runs under `dotnet <dll>` on any OS with a
// matching .NET 10 runtime installed; there is no per-RID binary matrix here
// the way the XRPL plugins have.

'use strict';

const path = require('node:path');
const os = require('node:os');
const fs = require('node:fs');
const { spawnSync, spawn } = require('node:child_process');

const SERVER_DLL = path.join(__dirname, 'server', 'CodeIndex.Server.dll');
const DEFAULT_APPSETTINGS = path.join(__dirname, 'server', 'appsettings.json');
const REQUIRED_NET_MAJOR = 10;

// Where the plugin looks for the user's project list. A JSON file is the
// natural shape for "a handful of {Id, Root} pairs" — expressing several
// projects purely through CODEINDEX_CodeIndex__Projects__N__* environment
// variables works but is clumsy to hand-edit. Overridable so a user who
// already manages configuration via ENV (CI, a shared machine, etc.) is not
// forced to also maintain a file.
const DEFAULT_CONFIG_PATH = path.join(os.homedir(), '.code-index-mcp', 'config.json');
const CONFIG_PATH = process.env.CODEINDEX_CONFIG_FILE || DEFAULT_CONFIG_PATH;

const OLLAMA_PROBE_TIMEOUT_MS = 5000;

function logError(...lines) {
  for (const line of lines) console.error(line);
}

// ── 1. .NET runtime ──────────────────────────────────────────────────────────

function checkDotnetRuntime() {
  let result;
  try {
    result = spawnSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' });
  } catch (err) {
    logError(
      '[code-index] Could not run `dotnet` — is the .NET runtime installed and on PATH?',
      `[code-index] Spawn error: ${err.message}`,
      '[code-index] Install the .NET 10 runtime: https://dotnet.microsoft.com/download/dotnet/10.0',
    );
    return false;
  }

  if (result.error || result.status !== 0) {
    logError(
      '[code-index] `dotnet --list-runtimes` failed — is the .NET runtime installed and on PATH?',
      '[code-index] Install the .NET 10 runtime: https://dotnet.microsoft.com/download/dotnet/10.0',
    );
    return false;
  }

  const hasNet10 = result.stdout
    .split(/\r?\n/)
    .some((line) => new RegExp(`^Microsoft\\.NETCore\\.App ${REQUIRED_NET_MAJOR}\\.`).test(line.trim()));

  if (!hasNet10) {
    logError(
      `[code-index] No .NET ${REQUIRED_NET_MAJOR}.x runtime found. Installed runtimes:`,
      result.stdout.trim() || '(none)',
      '',
      '[code-index] Install the .NET 10 runtime: https://dotnet.microsoft.com/download/dotnet/10.0',
    );
    return false;
  }

  return true;
}

// ── Configuration resolution (config file + appsettings.json defaults) ──────

/** Reads a JSON file, returning {} on any read/parse failure (never throws —
 * a malformed user config file is reported explicitly by the caller instead). */
function readJsonSafe(filePath) {
  try {
    return JSON.parse(fs.readFileSync(filePath, 'utf8'));
  } catch {
    return {};
  }
}

function flattenProjects(config, envOut) {
  const projects = config && config.CodeIndex && Array.isArray(config.CodeIndex.Projects)
    ? config.CodeIndex.Projects
    : [];

  projects.forEach((project, index) => {
    if (project.Id !== undefined) envOut[`CODEINDEX_CodeIndex__Projects__${index}__Id`] = String(project.Id);
    if (project.Root !== undefined) envOut[`CODEINDEX_CodeIndex__Projects__${index}__Root`] = String(project.Root);
    if (Array.isArray(project.Extensions)) {
      project.Extensions.forEach((ext, extIndex) => {
        envOut[`CODEINDEX_CodeIndex__Projects__${index}__Extensions__${extIndex}`] = String(ext);
      });
    }
    if (project.CacheDirectory !== undefined) {
      envOut[`CODEINDEX_CodeIndex__Projects__${index}__CacheDirectory`] = String(project.CacheDirectory);
    }
  });
}

function flattenEmbedding(config, envOut) {
  const embedding = (config && config.Embedding) || {};
  for (const [key, value] of Object.entries(embedding)) {
    if (value !== undefined && value !== null) {
      envOut[`CODEINDEX_Embedding__${key}`] = String(value);
    }
  }
}

/** Builds the environment the child process runs with: explicit CODEINDEX_
 * variables already present in this process's environment win, per key, over
 * anything derived from the config file — the file exists to make
 * multi-project setup convenient, not to shadow a value a user (or CI, or
 * the .mcp.json env block) deliberately set. */
function buildChildEnv() {
  const derived = {};
  const fileConfig = readJsonSafe(CONFIG_PATH);
  flattenProjects(fileConfig, derived);
  flattenEmbedding(fileConfig, derived);

  const env = { ...process.env };
  for (const [key, value] of Object.entries(derived)) {
    if (env[key] === undefined) env[key] = value;
  }
  return env;
}

function hasAnyProjectConfigured(env) {
  const pattern = /^CODEINDEX_CodeIndex__Projects__\d+__Root$/;
  return Object.keys(env).some((key) => pattern.test(key) && env[key] && env[key].trim() !== '');
}

function resolveEmbeddingSetting(env, key, fallback) {
  const envKey = `CODEINDEX_Embedding__${key}`;
  if (env[envKey]) return env[envKey];

  const defaults = readJsonSafe(DEFAULT_APPSETTINGS);
  const fromDefaults = defaults && defaults.Embedding && defaults.Embedding[key];
  return fromDefaults !== undefined ? fromDefaults : fallback;
}

function checkProjectConfigured(env) {
  if (hasAnyProjectConfigured(env)) return true;

  logError(
    '[code-index] No project is configured — there is nothing to search yet.',
    '',
    `[code-index] Create ${CONFIG_PATH} with at least one project:`,
    '',
    '  {',
    '    "CodeIndex": {',
    '      "Projects": [',
    '        { "Id": "myproject", "Root": "C:\\\\path\\\\to\\\\MyProject" }',
    '      ]',
    '    }',
    '  }',
    '',
    '[code-index] (Or set CODEINDEX_CodeIndex__Projects__0__Id / __Root as environment ' +
      'variables — see the plugin README for the full precedence rules.)',
    '',
    '[code-index] Then restart Claude Code so the server picks up the new configuration.',
  );
  return false;
}

// ── 2/3. Ollama reachable + model pulled ─────────────────────────────────────

async function fetchWithTimeout(url, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { signal: controller.signal });
  } finally {
    clearTimeout(timer);
  }
}

async function checkOllama(env) {
  const endpoint = resolveEmbeddingSetting(env, 'Endpoint', 'http://localhost:11434');
  const model = resolveEmbeddingSetting(env, 'Model', 'qwen3-embedding:4b');
  const tagsUrl = new URL('/api/tags', endpoint).toString();

  let response;
  try {
    response = await fetchWithTimeout(tagsUrl, OLLAMA_PROBE_TIMEOUT_MS);
  } catch {
    logError(
      `[code-index] Cannot reach Ollama at ${endpoint}.`,
      '',
      '[code-index] code-index-mcp needs Ollama running locally to compute embeddings.',
      '[code-index] Start it with:',
      '',
      '  ollama serve',
      '',
      '[code-index] Then ask your question again.',
    );
    return false;
  }

  if (!response.ok) {
    logError(
      `[code-index] Ollama at ${endpoint} responded with ${response.status} ${response.statusText} for /api/tags.`,
      '[code-index] Make sure Ollama is healthy (`ollama serve`) and try again.',
    );
    return false;
  }

  let body;
  try {
    body = await response.json();
  } catch {
    logError(`[code-index] Ollama at ${endpoint} returned a response /api/tags could not parse as JSON.`);
    return false;
  }

  const models = Array.isArray(body.models) ? body.models : [];
  const pulled = models.some((m) => m && (m.name === model || m.model === model));

  if (!pulled) {
    logError(
      `[code-index] Ollama is running, but model '${model}' is not pulled yet.`,
      '',
      '[code-index] Pull it (about 2.5 GB, one-time download):',
      '',
      `  ollama pull ${model}`,
      '',
      '[code-index] Then ask your question again.',
    );
    return false;
  }

  return true;
}

// ── Spawn the real server ────────────────────────────────────────────────────

function runServer(env) {
  if (!fs.existsSync(SERVER_DLL)) {
    logError(
      `[code-index] Server assembly not found: ${SERVER_DLL}`,
      '[code-index] Was this plugin installed correctly? Expected layout: bin/server/CodeIndex.Server.dll',
    );
    process.exit(2);
  }

  const args = [SERVER_DLL, ...process.argv.slice(2)];
  const child = spawn('dotnet', args, { stdio: 'inherit', env });

  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
    } else {
      process.exit(code ?? 0);
    }
  });

  child.on('error', (err) => {
    logError(`[code-index] Failed to spawn dotnet ${SERVER_DLL}: ${err.message}`);
    process.exit(3);
  });

  for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.on(sig, () => {
      if (!child.killed) child.kill(sig);
    });
  }
}

async function main() {
  if (!checkDotnetRuntime()) process.exit(2);

  const env = buildChildEnv();

  if (!checkProjectConfigured(env)) process.exit(2);
  if (!(await checkOllama(env))) process.exit(2);

  runServer(env);
}

main().catch((err) => {
  logError(`[code-index] Unexpected launcher error: ${err && err.stack ? err.stack : err}`);
  process.exit(1);
});
