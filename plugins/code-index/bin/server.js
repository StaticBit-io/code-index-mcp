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
// The published server build is NOT committed to this repository — it is
// fetched on demand from a GitHub Release, cached under
// ~/.code-index-mcp/server/<version>/, and verified against a checksum that
// ships inside the plugin package itself (bin/server.sha256). See
// ensureServerInstalled() below for the full flow and README.md's
// "Troubleshooting" section for the exact messages each failure path prints.
//
// Layout:
//   bin/server.js          <- this file
//   bin/server.sha256      <- expected sha256 of this version's release asset
//   ~/.code-index-mcp/server/<version>/CodeIndex.Server.dll (+ deps, appsettings.json)
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
const zlib = require('node:zlib');
const crypto = require('node:crypto');
const { spawnSync, spawn } = require('node:child_process');

const REQUIRED_NET_MAJOR = 10;

const PLUGIN_ROOT = path.join(__dirname, '..');
const PLUGIN_MANIFEST_PATH = path.join(PLUGIN_ROOT, '.claude-plugin', 'plugin.json');
const CHECKSUM_FILE_PATH = path.join(__dirname, 'server.sha256');

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

/** Reads a JSON file, returning {} when it simply doesn't exist (the normal
 * case for an as-yet-unconfigured install). A file that exists but can't be
 * read or doesn't parse as JSON is a real problem the user needs to see —
 * silently treating it as {} would surface as the misleading "No project is
 * configured" message instead of the actual cause, so those cases throw with
 * the path and original error attached for the caller to report. */
function readJsonSafe(filePath) {
  let raw;
  try {
    raw = fs.readFileSync(filePath, 'utf8');
  } catch (err) {
    if (err.code === 'ENOENT') return {};
    throw new Error(`Could not read ${filePath}: ${err.message}`);
  }

  try {
    return JSON.parse(raw);
  } catch (err) {
    throw new Error(`${filePath} is not valid JSON: ${err.message}`);
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

/** The exact optional overrides `.mcp.json`'s `env` block declares via `${VAR}` placeholders.
 * Claude Code substitutes an unset placeholder with an *empty string* rather than omitting the
 * key, so every user who never customized one of these three arrives here with e.g.
 * `CODEINDEX_Embedding__Endpoint=""` — a value that is never meaningful for any of them (a URI, a
 * model name, and a file path can none of them usefully be `""`), but which both this launcher's
 * own "env wins over config file" merge below and .NET's environment-variable configuration
 * provider treat as an explicit, deliberately-set override — the former skips the config-file
 * value entirely (see buildChildEnv), the latter clobbers EmbeddingOptions' compiled-in default
 * and feeds `new Uri("")` (see CodeIndex.Core.Embedding.EmbeddingOptions.Validate for the
 * server-side half of this fix).
 *
 * Only these three declared names are stripped — not every empty `CODEINDEX_*` variable — because
 * at least one other setting in this same namespace treats an empty string as meaningful, load-
 * bearing configuration rather than "unset": `Embedding:QueryInstruction` is documented to mean
 * "send the bare query with no prefix" when empty. A blanket rule would silently reset that back
 * to its non-empty default for a user who set it that way on purpose. Stripping by exact name is
 * narrow and predictable: it fixes exactly the shape this launcher's own `.mcp.json` can produce,
 * and nothing a user sets directly and deliberately. */
const OPTIONAL_ENV_OVERRIDES = [
  'CODEINDEX_CONFIG_FILE',
  'CODEINDEX_Embedding__Endpoint',
  'CODEINDEX_Embedding__Model',
];

/** Returns a shallow copy of `sourceEnv` with any of OPTIONAL_ENV_OVERRIDES removed when their
 * value is exactly `''` — the shape Claude Code produces for an unset `${VAR}` placeholder. A real
 * override (any non-empty string) passes through untouched, and every other environment variable
 * (including other CODEINDEX_* ones) is never inspected at all. */
function stripEmptyOptionalOverrides(sourceEnv) {
  const result = { ...sourceEnv };
  for (const key of OPTIONAL_ENV_OVERRIDES) {
    if (result[key] === '') delete result[key];
  }
  return result;
}

/** Builds the environment the child process runs with: explicit CODEINDEX_
 * variables already present in this process's environment win, per key, over
 * anything derived from the config file — the file exists to make
 * multi-project setup convenient, not to shadow a value a user (or CI, or
 * the .mcp.json env block) deliberately set. An empty-string override from
 * `.mcp.json`'s own env block (see OPTIONAL_ENV_OVERRIDES/stripEmptyOptionalOverrides above) is
 * stripped first, so it behaves as genuinely unset — falling through to the config-file-derived
 * value when there is one, or to the server's own compiled-in default when there isn't — instead
 * of shadowing either with `""`. */
function buildChildEnv() {
  const derived = {};
  const fileConfig = readJsonSafe(CONFIG_PATH);
  flattenProjects(fileConfig, derived);
  flattenEmbedding(fileConfig, derived);

  const env = stripEmptyOptionalOverrides(process.env);
  for (const [key, value] of Object.entries(derived)) {
    if (env[key] === undefined) env[key] = value;
  }
  return env;
}

function hasAnyProjectConfigured(env) {
  const pattern = /^CODEINDEX_CodeIndex__Projects__\d+__Root$/;
  return Object.keys(env).some((key) => pattern.test(key) && env[key] && env[key].trim() !== '');
}

/** `serverDir` is where this version's appsettings.json lives once installed
 * (see ensureServerInstalled). Before that directory exists there is nothing
 * to read here — callers only need this after the server is in place, since
 * it's only used for the Ollama preflight, which runs after installation. */
function resolveEmbeddingSetting(env, key, fallback, serverDir) {
  const envKey = `CODEINDEX_Embedding__${key}`;
  if (env[envKey]) return env[envKey];

  const defaults = readJsonSafe(path.join(serverDir, 'appsettings.json'));
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

async function checkOllama(env, serverDir) {
  const endpoint = resolveEmbeddingSetting(env, 'Endpoint', 'http://localhost:11434', serverDir);
  const model = resolveEmbeddingSetting(env, 'Model', 'qwen3-embedding:4b', serverDir);
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

// ── Server binary: fetch from a GitHub Release, cache, verify ───────────────
//
// Design notes (see also README.md → "How the server binary is fetched"):
//
// - Version source: plugins/code-index/.claude-plugin/plugin.json carries a
//   dedicated `serverVersion` field, deliberately separate from the plugin's
//   own `version`. The plugin version covers the whole package (skills,
//   docs, this launcher script); the server version identifies exactly which
//   published server release asset to run. They move together today but
//   don't have to — a docs/skill-only plugin bump shouldn't force a redundant
//   14 MB re-download, and a server-only rebuild shouldn't force a marketplace
//   version bump just to get users to pick it up.
// - Cache location: ~/.code-index-mcp/server/<version>/, sibling to the
//   existing ~/.code-index-mcp/config.json — same trust/home directory the
//   rest of the plugin already uses.
// - Checksum location: bin/server.sha256, committed in this repository and
//   shipped as part of the plugin package itself. The repository is the
//   trust anchor: the same commit that bumps serverVersion carries the
//   checksum of the release it points to, so verification never depends on
//   anything fetched over the network.
// - Concurrency: install into a uniquely-named temp directory under the same
//   cache root (so the final `fs.renameSync` is same-filesystem and atomic),
//   write a `.verified-sha256` marker as the last file before renaming, then
//   rename the temp directory onto the final path. A reader only ever
//   observes the final path as either "absent" or "one complete, verified
//   install" — a process killed mid-download or mid-extract only ever leaves
//   debris in its own temp directory, never at the path other launchers
//   check. If our rename loses a race to a concurrent install of the same
//   version, we detect the already-valid target and just use it.
// - Private-repo auth: every GitHub API call optionally carries a bearer
//   token (CODEINDEX_GITHUB_TOKEN, then GH_TOKEN/GITHUB_TOKEN, then
//   `gh auth token` if the GitHub CLI is installed and logged in). This
//   works unchanged the day the repository goes public — no token resolves,
//   none is sent, and anonymous access (subject to GitHub's normal
//   unauthenticated rate limit) just works.

const RELEASE_OWNER = 'StaticBit-io';
const RELEASE_REPO = 'code-index-mcp';
const GITHUB_API_BASE = 'https://api.github.com';
const GITHUB_ISSUES_URL = `https://github.com/${RELEASE_OWNER}/${RELEASE_REPO}/issues`;

const API_TIMEOUT_MS = 15000;
const DOWNLOAD_TIMEOUT_MS = 300000; // 5 min — generous for ~14 MB on a slow link
const STALE_TEMP_MAX_AGE_MS = 60 * 60 * 1000; // 1 hour

const SERVER_CACHE_ROOT = path.join(os.homedir(), '.code-index-mcp', 'server');
const VERIFIED_MARKER_NAME = '.verified-sha256';

/** Thrown (never `process.exit()`ed directly) by every failure path that can
 * run after a `fetch()` call in this process. On this Node/Windows
 * combination, calling `process.exit()` shortly after any fetch — even a
 * completed, successfully-cleaned-up one — reliably crashes the process
 * with a libuv assertion (`UV_HANDLE_CLOSING`) instead of exiting with the
 * intended code, because undici's connection-pool teardown races the
 * synchronous exit. Throwing this and letting `main()`'s single top-level
 * catch set `process.exitCode` and return avoids calling `process.exit()`
 * at all on that path, which sidesteps the race entirely (verified: the
 * same fetch-then-`process.exitCode = n`-then-return sequence does not
 * crash, where fetch-then-`process.exit(n)` reliably does). */
class LauncherExit extends Error {
  constructor(code) {
    super(`launcher exit ${code}`);
    this.code = code;
  }
}

class NetworkError extends Error {}

class HttpStatusError extends Error {
  constructor(status, body) {
    super(`HTTP ${status}`);
    this.status = status;
    this.body = body;
  }
}

class ChecksumMismatchError extends Error {
  constructor(expected, actual) {
    super(`checksum mismatch (expected ${expected}, got ${actual})`);
    this.expected = expected;
    this.actual = actual;
  }
}

function releaseTag(version) {
  return `server-v${version}`;
}

function assetFileName(version) {
  return `code-index-server-${version}.tar.gz`;
}

function releasePageUrl(version) {
  return `https://github.com/${RELEASE_OWNER}/${RELEASE_REPO}/releases/tag/${releaseTag(version)}`;
}

function assetDownloadUrl(version) {
  return `https://github.com/${RELEASE_OWNER}/${RELEASE_REPO}/releases/download/${releaseTag(version)}/${assetFileName(version)}`;
}

function cacheDirFor(version) {
  return path.join(SERVER_CACHE_ROOT, version);
}

function readExpectedChecksum(version) {
  let raw;
  try {
    raw = fs.readFileSync(CHECKSUM_FILE_PATH, 'utf8');
  } catch (err) {
    throw new Error(
      `This plugin build is missing its server checksum file (${CHECKSUM_FILE_PATH}) — cannot safely ` +
        'verify a downloaded server binary. Reinstall the plugin from the marketplace; if this persists, ' +
        `please report it at ${GITHUB_ISSUES_URL}.`,
    );
  }

  const match = raw.trim().match(/^([0-9a-fA-F]{64})\s+(\S+)$/);
  if (!match) {
    throw new Error(`${CHECKSUM_FILE_PATH} is not in the expected "<sha256>  <filename>" format.`);
  }

  const [, hex, fileName] = match;
  const expectedFileName = assetFileName(version);
  if (fileName !== expectedFileName) {
    throw new Error(
      `${CHECKSUM_FILE_PATH} names "${fileName}" but the plugin manifest expects server v${version} ` +
        `(${expectedFileName}) — this plugin package looks inconsistent. Reinstall it from the marketplace.`,
    );
  }

  return { hex: hex.toLowerCase(), fileName };
}

function isCacheReady(cacheDir, expectedHex) {
  try {
    const dll = path.join(cacheDir, 'CodeIndex.Server.dll');
    if (!fs.existsSync(dll)) return false;
    const marker = fs.readFileSync(path.join(cacheDir, VERIFIED_MARKER_NAME), 'utf8').trim().toLowerCase();
    return marker === expectedHex.toLowerCase();
  } catch {
    return false;
  }
}

function removeDirBestEffort(dir) {
  try {
    fs.rmSync(dir, { recursive: true, force: true });
  } catch {
    // best-effort cleanup only
  }
}

function sweepStaleTempDirs() {
  try {
    for (const name of fs.readdirSync(SERVER_CACHE_ROOT)) {
      if (!name.startsWith('.tmp-install-')) continue;
      const full = path.join(SERVER_CACHE_ROOT, name);
      let stat;
      try {
        stat = fs.statSync(full);
      } catch {
        continue;
      }
      if (Date.now() - stat.mtimeMs > STALE_TEMP_MAX_AGE_MS) removeDirBestEffort(full);
    }
  } catch {
    // best-effort cleanup only — a missing/unreadable cache root is fine here
  }
}

function resolveGitHubToken() {
  const fromEnv = process.env.CODEINDEX_GITHUB_TOKEN || process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
  if (fromEnv && fromEnv.trim()) return fromEnv.trim();

  try {
    const result = spawnSync('gh', ['auth', 'token'], { encoding: 'utf8', timeout: 3000 });
    if (result.status === 0 && result.stdout && result.stdout.trim()) return result.stdout.trim();
  } catch {
    // `gh` not installed, not on PATH, or not logged in — proceed without a token.
  }
  return undefined;
}

function buildGithubHeaders(token, accept) {
  const headers = {
    'User-Agent': 'code-index-mcp-launcher',
    Accept: accept,
    'X-GitHub-Api-Version': '2022-11-28',
  };
  if (token) headers.Authorization = `Bearer ${token}`;
  return headers;
}

async function fetchWithAbort(url, options, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    return await fetch(url, { ...options, signal: controller.signal });
  } catch (err) {
    throw new NetworkError(err.message);
  } finally {
    clearTimeout(timer);
  }
}

async function githubJson(url, token) {
  const response = await fetchWithAbort(
    url,
    { headers: buildGithubHeaders(token, 'application/vnd.github+json') },
    API_TIMEOUT_MS,
  );
  if (!response.ok) {
    let body = '';
    try {
      body = await response.text();
    } catch {
      // ignore — status code alone is enough to report
    }
    throw new HttpStatusError(response.status, body);
  }
  return response.json();
}

async function resolveReleaseAsset(version, token) {
  const tag = releaseTag(version);
  const url = `${GITHUB_API_BASE}/repos/${RELEASE_OWNER}/${RELEASE_REPO}/releases/tags/${tag}`;
  const release = await githubJson(url, token);

  const fileName = assetFileName(version);
  const asset = (release.assets || []).find((a) => a.name === fileName);
  if (!asset) {
    const err = new Error(`Release ${tag} exists but has no asset named ${fileName}.`);
    err.code = 'ASSET_NOT_IN_RELEASE';
    err.tag = tag;
    throw err;
  }
  return asset;
}

function logDownloadProgress(received, total) {
  const mb = (n) => (n / (1024 * 1024)).toFixed(1);
  if (total > 0) {
    const pct = Math.min(100, Math.round((received / total) * 100));
    process.stderr.write(`\r[code-index] Downloaded ${mb(received)} MB / ${mb(total)} MB (${pct}%)`);
  } else {
    process.stderr.write(`\r[code-index] Downloaded ${mb(received)} MB`);
  }
}

/** Downloads a release asset by its GitHub API asset id. Fetches the asset
 * endpoint with `redirect: 'manual'` and, if GitHub answers with a redirect
 * to a pre-signed storage URL (the common case for anything but tiny
 * assets), follows it in a *separate* unauthenticated request. Blob storage
 * behind a pre-signed URL commonly rejects a request that carries both a
 * signature in the query string and an Authorization header — sending our
 * GitHub token along on the redirect would break exactly the private-repo
 * case it's meant to support. */
async function downloadAssetBuffer(assetId, token, expectedSize) {
  const assetUrl = `${GITHUB_API_BASE}/repos/${RELEASE_OWNER}/${RELEASE_REPO}/releases/assets/${assetId}`;

  let response = await fetchWithAbort(
    assetUrl,
    { redirect: 'manual', headers: buildGithubHeaders(token, 'application/octet-stream') },
    DOWNLOAD_TIMEOUT_MS,
  );

  if (response.status >= 300 && response.status < 400) {
    const location = response.headers.get('location');
    if (!location) throw new HttpStatusError(response.status, 'redirect response with no Location header');
    response = await fetchWithAbort(location, { redirect: 'follow' }, DOWNLOAD_TIMEOUT_MS);
  }

  if (!response.ok) {
    let body = '';
    try {
      body = await response.text();
    } catch {
      // ignore
    }
    throw new HttpStatusError(response.status, body);
  }

  const total = Number(response.headers.get('content-length')) || expectedSize || 0;
  const chunks = [];
  let received = 0;
  let lastLog = 0;

  try {
    const reader = response.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      received += value.length;
      const now = Date.now();
      if (now - lastLog > 500) {
        lastLog = now;
        logDownloadProgress(received, total);
      }
    }
  } catch (err) {
    throw new NetworkError(err.message);
  }

  logDownloadProgress(received, total);
  process.stderr.write('\n');
  return Buffer.concat(chunks);
}

function readCString(buf, start, len) {
  const slice = buf.subarray(start, start + len);
  const nul = slice.indexOf(0);
  return (nul === -1 ? slice : slice.subarray(0, nul)).toString('utf8');
}

/** Minimal reader for the tar entries CI produces (`tar czf archive.tar.gz *`
 * from the publish output directory): a flat list of regular files, short
 * (ustar-header-sized) names, no long-name/pax extensions. Deliberately not
 * a general-purpose tar implementation — just enough to unpack our own
 * release asset without adding a dependency. The whole-archive sha256 is
 * already verified before this ever runs; the per-header checksum check
 * below is extra insurance against a bug in the offset arithmetic here, not
 * a security boundary. */
function parseTarBuffer(buf) {
  const entries = [];
  let offset = 0;

  while (offset + 512 <= buf.length) {
    const header = buf.subarray(offset, offset + 512);
    if (header.every((b) => b === 0)) break; // end-of-archive marker

    const name = readCString(header, 0, 100);
    const sizeField = readCString(header, 124, 12).trim();
    const typeFlag = String.fromCharCode(header[156]);
    const prefix = readCString(header, 345, 155);
    const checksumField = readCString(header, 148, 8).trim();

    const size = sizeField === '' ? 0 : parseInt(sizeField, 8);

    const expectedChecksum = parseInt(checksumField, 8);
    let sum = 0;
    for (let i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 /* space, per spec */ : header[i];
    if (!Number.isNaN(expectedChecksum) && sum !== expectedChecksum) {
      throw new Error(`Downloaded archive is corrupt (bad tar header checksum at offset ${offset}).`);
    }

    const fullName = prefix ? `${prefix}/${name}` : name;
    const dataStart = offset + 512;
    const dataEnd = dataStart + size;

    if (typeFlag === '0' || typeFlag === '\0') {
      entries.push({ name: fullName, data: buf.subarray(dataStart, dataEnd) });
    }
    // typeFlag '5' (directory) and others are skipped — our archive is a flat
    // file list and fs.mkdirSync(..., { recursive: true }) below creates any
    // parent directories a file entry needs regardless of whether the
    // archive also carries explicit directory entries for them.

    offset = dataStart + Math.ceil(size / 512) * 512;
  }

  return entries;
}

function extractTarGz(buffer) {
  let tarBuf;
  try {
    tarBuf = zlib.gunzipSync(buffer);
  } catch (err) {
    throw new Error(`Downloaded archive is not valid gzip: ${err.message}`);
  }
  return parseTarBuffer(tarBuf);
}

/** Writes extracted tar entries under destDir, refusing (zip-slip) any entry
 * whose name would resolve outside destDir — an absolute path (`/etc/passwd`,
 * `C:\Windows\...`) or a `../` traversal. The sha256 check in
 * installServerVersion runs before this and is the primary trust boundary,
 * but this is the actual write path, so it gets its own independent check
 * rather than relying solely on the checksum holding forever (a future
 * caller of this function, or a relaxed check, shouldn't silently regain the
 * ability to write outside destDir). parseTarBuffer already drops anything
 * that isn't a regular file (typeFlag '0'/'\0'), so symlink entries never
 * reach here in the first place. Throws and writes nothing further on the
 * first bad entry — a hostile archive is refused outright, not partially
 * extracted with the bad entry skipped. */
function extractEntriesTo(entries, destDir) {
  const destRoot = path.resolve(destDir);
  for (const entry of entries) {
    if (path.isAbsolute(entry.name)) {
      throw new Error(`Downloaded archive contains an unsafe entry path: ${entry.name}`);
    }
    const target = path.resolve(destRoot, entry.name);
    const rel = path.relative(destRoot, target);
    if (rel.startsWith('..') || path.isAbsolute(rel)) {
      throw new Error(`Downloaded archive contains an unsafe entry path: ${entry.name}`);
    }
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, entry.data);
  }
}

function publishCacheDir(tempDir, finalDir, expectedHex, version) {
  try {
    fs.renameSync(tempDir, finalDir);
  } catch (err) {
    // Another launcher instance may have installed this exact version first
    // (two Claude Code windows starting at once). Re-check ground truth
    // rather than assuming our rename losing the race means real failure.
    if (isCacheReady(finalDir, expectedHex)) {
      logError(`[code-index] Server v${version} was installed by a concurrent process — using it.`);
      return;
    }
    throw err;
  }
}

async function installServerVersion(version, expected, cacheDir) {
  const token = resolveGitHubToken();

  logError(`[code-index] Server v${version} not found in local cache — downloading from GitHub Releases (~14 MB, one-time)...`);

  const asset = await resolveReleaseAsset(version, token);
  const buffer = await downloadAssetBuffer(asset.id, token, asset.size);

  logError('[code-index] Verifying checksum...');
  const actualHex = crypto.createHash('sha256').update(buffer).digest('hex');
  if (actualHex.toLowerCase() !== expected.hex.toLowerCase()) {
    throw new ChecksumMismatchError(expected.hex, actualHex);
  }
  logError('[code-index] Checksum OK — extracting...');

  const entries = extractTarGz(buffer);

  const tempDir = path.join(
    SERVER_CACHE_ROOT,
    `.tmp-install-${version}-${process.pid}-${crypto.randomBytes(4).toString('hex')}`,
  );
  fs.mkdirSync(tempDir, { recursive: true });

  try {
    extractEntriesTo(entries, tempDir);
    // Written last, inside the temp dir, so it only ever appears at the
    // final path as part of the one atomic rename below — never before the
    // rest of the extraction has finished.
    fs.writeFileSync(path.join(tempDir, VERIFIED_MARKER_NAME), expected.hex, 'utf8');
    publishCacheDir(tempDir, cacheDir, expected.hex, version);
  } finally {
    removeDirBestEffort(tempDir); // no-op once renamed away
  }

  logError(`[code-index] Server v${version} installed at ${cacheDir}${path.sep}`);
}

function logInstallError(version, expected, err) {
  const assetUrl = assetDownloadUrl(version);
  const cacheDir = cacheDirFor(version);

  if (err instanceof NetworkError) {
    logError(
      `[code-index] Server v${version} is not installed yet, and GitHub could not be reached to download it.`,
      `[code-index] Network error: ${err.message}`,
      '',
      '[code-index] Check your internet connection and try again. If you are offline, download the release',
      '[code-index] manually and extract it into the folder below:',
      '',
      `  ${assetUrl}`,
      '',
      `  ${cacheDir}${path.sep}`,
      '',
      '[code-index] Then ask your question again — the launcher will find it there and skip the download.',
    );
    return;
  }

  if (err instanceof ChecksumMismatchError) {
    logError(
      `[code-index] Downloaded server v${version} but its checksum does not match — refusing to run it.`,
      `[code-index]   expected: ${err.expected}`,
      `[code-index]   actual:   ${err.actual}`,
      '',
      '[code-index] This usually means a corrupted download or a compromised release asset. The file was',
      '[code-index] not installed. Try again; if this keeps happening, please report it:',
      '',
      `  ${GITHUB_ISSUES_URL}`,
    );
    return;
  }

  if (err instanceof HttpStatusError && err.status === 404) {
    logError(
      `[code-index] No GitHub release found for server v${version} (tag ${releaseTag(version)}).`,
      '',
      '[code-index] This plugin build expects a matching server release that is not published — check',
      `[code-index]   https://github.com/${RELEASE_OWNER}/${RELEASE_REPO}/releases`,
      '[code-index] for available versions, or download it manually once published:',
      '',
      `  ${assetUrl}`,
    );
    return;
  }

  if (err instanceof HttpStatusError && (err.status === 401 || err.status === 403)) {
    logError(
      `[code-index] GitHub returned ${err.status} while requesting the server v${version} release.`,
      '[code-index] This repository is private and needs authentication to download release assets.',
      '',
      "[code-index] Provide a token with 'repo' scope one of these ways:",
      '[code-index]   - set CODEINDEX_GITHUB_TOKEN (or GH_TOKEN / GITHUB_TOKEN) in your environment, or',
      '[code-index]   - authenticate the GitHub CLI (`gh auth login`) — the launcher borrows its token automatically',
      '',
      '[code-index] Or download the asset manually with your browser and extract it into:',
      '',
      `  ${cacheDir}${path.sep}`,
      '',
      `  ${assetUrl}`,
    );
    return;
  }

  if (err && err.code === 'ASSET_NOT_IN_RELEASE') {
    logError(
      `[code-index] Release ${err.tag} exists but does not contain ${assetFileName(version)}.`,
      `[code-index] See ${releasePageUrl(version)} for what it does contain — the CI publish step may still be running.`,
    );
    return;
  }

  logError(
    `[code-index] Could not install server v${version}: ${err && err.message ? err.message : err}`,
    `[code-index] Manual fallback — download and extract into ${cacheDir}${path.sep}:`,
    '',
    `  ${assetUrl}`,
  );
}

/** Resolves the directory containing CodeIndex.Server.dll for this session,
 * fetching and caching it from GitHub Releases if it isn't already present.
 * Throws LauncherExit(2) on any failure — every path here is a precondition
 * for starting the server at all; main()'s top-level catch sets
 * process.exitCode and returns rather than calling process.exit() directly
 * (see the LauncherExit class comment for why). */
async function ensureServerInstalled() {
  const devOverride = process.env.CODEINDEX_SERVER_DIR;
  if (devOverride) {
    const dll = path.join(devOverride, 'CodeIndex.Server.dll');
    if (!fs.existsSync(dll)) {
      logError(
        `[code-index] CODEINDEX_SERVER_DIR is set to ${devOverride}, but ${dll} does not exist.`,
        '[code-index] Point it at a published CodeIndex.Server build directory, or unset it to use the normal download path.',
      );
      throw new LauncherExit(2);
    }
    logError(`[code-index] Using local server build at ${devOverride} (CODEINDEX_SERVER_DIR override — no download, no verification).`);
    return devOverride;
  }

  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(PLUGIN_MANIFEST_PATH, 'utf8'));
  } catch (err) {
    logError(`[code-index] Could not read plugin manifest (${PLUGIN_MANIFEST_PATH}): ${err.message}`);
    throw new LauncherExit(2);
  }

  const version = manifest.serverVersion;
  if (!version) {
    logError('[code-index] Plugin manifest is missing "serverVersion" — this plugin build cannot resolve which server to run.');
    throw new LauncherExit(2);
  }

  let expected;
  try {
    expected = readExpectedChecksum(version);
  } catch (err) {
    logError(`[code-index] ${err.message}`);
    throw new LauncherExit(2);
  }

  const cacheDir = cacheDirFor(version);
  if (isCacheReady(cacheDir, expected.hex)) {
    return cacheDir; // fast path — already installed, no network at all
  }

  try {
    fs.mkdirSync(SERVER_CACHE_ROOT, { recursive: true });
    sweepStaleTempDirs();
    await installServerVersion(version, expected, cacheDir);
    return cacheDir;
  } catch (err) {
    if (err instanceof LauncherExit) throw err;
    logInstallError(version, expected, err);
    throw new LauncherExit(2);
  }
}

// ── Spawn the real server ────────────────────────────────────────────────────

function runServer(env, serverDir) {
  const serverDll = path.join(serverDir, 'CodeIndex.Server.dll');
  if (!fs.existsSync(serverDll)) {
    logError(
      `[code-index] Server assembly not found: ${serverDll}`,
      '[code-index] The installed server directory looks incomplete or corrupted — delete it and retry:',
      '',
      `  ${serverDir}`,
    );
    throw new LauncherExit(2);
  }

  const args = [serverDll, ...process.argv.slice(2)];
  const child = spawn('dotnet', args, { stdio: 'inherit', env });

  child.on('exit', (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
    } else {
      process.exit(code ?? 0);
    }
  });

  child.on('error', (err) => {
    logError(`[code-index] Failed to spawn dotnet ${serverDll}: ${err.message}`);
    process.exit(3);
  });

  for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP']) {
    process.on(sig, () => {
      if (!child.killed) child.kill(sig);
    });
  }
}

async function main() {
  if (!checkDotnetRuntime()) throw new LauncherExit(2);

  let env;
  try {
    env = buildChildEnv();
  } catch (err) {
    logError(`[code-index] ${err.message}`);
    throw new LauncherExit(2);
  }

  if (!checkProjectConfigured(env)) throw new LauncherExit(2);

  // Runs after the cheap, network-free checks above so a broken/unconfigured
  // install fails fast without spending bandwidth on a 14 MB download it
  // wouldn't have needed anyway.
  const serverDir = await ensureServerInstalled();

  let ollamaOk;
  try {
    ollamaOk = await checkOllama(env, serverDir);
  } catch (err) {
    logError(`[code-index] ${err.message}`);
    throw new LauncherExit(2);
  }
  if (!ollamaOk) throw new LauncherExit(2);

  runServer(env, serverDir);
}

if (require.main === module) {
  main().catch((err) => {
    // Every known failure path above throws LauncherExit instead of calling
    // process.exit() directly — see its class comment for why: on this
    // Node/Windows combination, process.exit() shortly after any fetch()
    // call in the process (the GitHub API/download calls almost always ran
    // by the time anything here fails) reliably crashes with a libuv
    // assertion instead of exiting with the intended code. Setting
    // process.exitCode and returning lets Node exit on its own once the
    // event loop drains, which does not race that teardown. (runServer's
    // child process exit/error handlers are the one place that still call
    // process.exit() directly — by then SIGINT/SIGTERM/SIGHUP listeners are
    // registered, which keep the event loop alive on their own, so an
    // explicit exit is genuinely required there, not just convenient.)
    if (err instanceof LauncherExit) {
      process.exitCode = err.code;
      return;
    }
    logError(`[code-index] Unexpected launcher error: ${err && err.stack ? err.stack : err}`);
    process.exitCode = 1;
  });
}

// Exported for server.test.js. Everything here is pure/deterministic (no
// network, no process.exit) so it can be unit-tested without spinning up a
// GitHub release or a .NET runtime; the network-touching orchestration
// (installServerVersion, ensureServerInstalled, main) is exercised instead
// via CODEINDEX_SERVER_DIR / a real cache directory in integration checks.
module.exports = {
  releaseTag,
  assetFileName,
  releasePageUrl,
  assetDownloadUrl,
  cacheDirFor,
  readExpectedChecksum,
  isCacheReady,
  publishCacheDir,
  parseTarBuffer,
  extractTarGz,
  extractEntriesTo,
  readCString,
  buildGithubHeaders,
  LauncherExit,
  NetworkError,
  HttpStatusError,
  ChecksumMismatchError,
  logInstallError,
  VERIFIED_MARKER_NAME,
  SERVER_CACHE_ROOT,
  CHECKSUM_FILE_PATH,
  PLUGIN_MANIFEST_PATH,
  RELEASE_OWNER,
  RELEASE_REPO,
  OPTIONAL_ENV_OVERRIDES,
  stripEmptyOptionalOverrides,
  buildChildEnv,
  CONFIG_PATH,
};
