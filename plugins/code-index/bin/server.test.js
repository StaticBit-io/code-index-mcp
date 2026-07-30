// Unit tests for the fetch/cache/verify machinery in server.js.
//
// Run with: node --test plugins/code-index/bin/server.test.js
//
// Scope: everything here is pure/deterministic — no network, no `dotnet`, no
// live GitHub release. The full install flow (download, checksum-verify a
// real release asset, extract, run) is exercised separately as a manual
// integration check against an actual published release; see the PR
// description / CHANGELOG for what was run there.

'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const zlib = require('node:zlib');
const crypto = require('node:crypto');

const srv = require('./server.js');

function mkTempDir(t, prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

test('URL/tag/filename builders are consistent with each other', () => {
  const version = '0.2.0';
  assert.equal(srv.releaseTag(version), 'server-v0.2.0');
  assert.equal(srv.assetFileName(version), 'code-index-server-0.2.0.tar.gz');
  assert.equal(
    srv.releasePageUrl(version),
    `https://github.com/${srv.RELEASE_OWNER}/${srv.RELEASE_REPO}/releases/tag/server-v0.2.0`,
  );
  assert.equal(
    srv.assetDownloadUrl(version),
    `https://github.com/${srv.RELEASE_OWNER}/${srv.RELEASE_REPO}/releases/download/server-v0.2.0/code-index-server-0.2.0.tar.gz`,
  );
  assert.equal(srv.cacheDirFor(version), path.join(os.homedir(), '.code-index-mcp', 'server', version));
});

test('buildGithubHeaders omits Authorization when no token is given, includes it when one is', () => {
  const noToken = srv.buildGithubHeaders(undefined, 'application/json');
  assert.equal(noToken.Authorization, undefined);
  assert.equal(noToken.Accept, 'application/json');
  assert.ok(noToken['User-Agent']);

  const withToken = srv.buildGithubHeaders('secret123', 'application/json');
  assert.equal(withToken.Authorization, 'Bearer secret123');
});

test('readExpectedChecksum: happy path parses "<hex>  <filename>"', (t) => {
  // Exercises the actual exported function (not a copy of its regex) by
  // requiring an isolated copy of the module pointed at a throwaway
  // server.sha256 — the same technique the mismatch/missing-file tests below
  // already use, so a real drift between the parser and this test would show
  // up here instead of only in those two.
  const dir = mkTempDir(t, 'code-index-checksum-ok-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });

  const version = '9.9.9';
  const hex = 'a'.repeat(64);
  fs.writeFileSync(path.join(binDir, 'server.sha256'), `${hex}  code-index-server-${version}.tar.gz\n`);
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: version }));

  const serverJsCopy = path.join(binDir, 'server.js');
  fs.copyFileSync(path.join(__dirname, 'server.js'), serverJsCopy);
  delete require.cache[require.resolve(serverJsCopy)];
  const isolated = require(serverJsCopy);

  assert.deepEqual(isolated.readExpectedChecksum(version), {
    hex,
    fileName: `code-index-server-${version}.tar.gz`,
  });
  delete require.cache[require.resolve(serverJsCopy)];
});

test('readExpectedChecksum: missing file throws a "broken plugin package" message naming the path', (t) => {
  // CHECKSUM_FILE_PATH is a const captured at module load from __dirname —
  // to exercise the real ENOENT branch, run an isolated copy of the module
  // from a bin/ directory that has no server.sha256 next to it at all.
  const dir = mkTempDir(t, 'code-index-nochecksum-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: '1.2.3' }));

  const serverJsCopy = path.join(binDir, 'server.js');
  fs.copyFileSync(path.join(__dirname, 'server.js'), serverJsCopy);
  delete require.cache[require.resolve(serverJsCopy)];
  const isolated = require(serverJsCopy);

  assert.throws(() => isolated.readExpectedChecksum('1.2.3'), /missing its server checksum file/);
  delete require.cache[require.resolve(serverJsCopy)];
});

test('readExpectedChecksum throws when the file exists but the version does not match its declared filename', (t) => {
  // This exercises the actual exported function, so it needs
  // CHECKSUM_FILE_PATH to point at a real file — set up a throwaway plugin
  // layout and require a fresh copy of the module pointed at it.
  const dir = mkTempDir(t, 'code-index-mismatch-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.writeFileSync(path.join(binDir, 'server.sha256'), `${'b'.repeat(64)}  code-index-server-1.0.0.tar.gz\n`);
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: '2.0.0' }));

  const serverJsCopy = path.join(binDir, 'server.js');
  fs.copyFileSync(path.join(__dirname, 'server.js'), serverJsCopy);
  delete require.cache[require.resolve(serverJsCopy)];
  const isolated = require(serverJsCopy);

  assert.throws(() => isolated.readExpectedChecksum('2.0.0'), /looks inconsistent/);
  delete require.cache[require.resolve(serverJsCopy)];
});

test('isCacheReady: false when directory is absent, empty, or has a non-matching marker; true only when both dll and matching marker exist', (t) => {
  const dir = mkTempDir(t, 'code-index-cache-');
  const cacheDir = path.join(dir, '0.2.0');
  const hex = 'c'.repeat(64);

  assert.equal(srv.isCacheReady(cacheDir, hex), false, 'absent directory');

  fs.mkdirSync(cacheDir, { recursive: true });
  assert.equal(srv.isCacheReady(cacheDir, hex), false, 'empty directory, no dll');

  fs.writeFileSync(path.join(cacheDir, 'CodeIndex.Server.dll'), 'stub');
  assert.equal(srv.isCacheReady(cacheDir, hex), false, 'dll present but no marker at all');

  fs.writeFileSync(path.join(cacheDir, srv.VERIFIED_MARKER_NAME), 'd'.repeat(64));
  assert.equal(srv.isCacheReady(cacheDir, hex), false, 'marker present but does not match expected hex');

  fs.writeFileSync(path.join(cacheDir, srv.VERIFIED_MARKER_NAME), hex.toUpperCase());
  assert.equal(srv.isCacheReady(cacheDir, hex), true, 'marker matches case-insensitively');
});

test('publishCacheDir: simulates two racing installs of the same version — the loser detects the winner and does not throw', (t) => {
  const root = mkTempDir(t, 'code-index-race-');
  const finalDir = path.join(root, '0.2.0');
  const hex = 'e'.repeat(64);

  // Winner: installs first.
  const winnerTemp = path.join(root, '.tmp-install-winner');
  fs.mkdirSync(winnerTemp, { recursive: true });
  fs.writeFileSync(path.join(winnerTemp, 'CodeIndex.Server.dll'), 'winner-bytes');
  fs.writeFileSync(path.join(winnerTemp, srv.VERIFIED_MARKER_NAME), hex);
  srv.publishCacheDir(winnerTemp, finalDir, hex, '0.2.0');
  assert.equal(srv.isCacheReady(finalDir, hex), true);
  assert.equal(fs.existsSync(winnerTemp), false, 'winner temp dir is gone (renamed away)');

  // Loser: finishes its own independent download/extract into a *different*
  // temp dir, then tries to publish to the same final path.
  const loserTemp = path.join(root, '.tmp-install-loser');
  fs.mkdirSync(loserTemp, { recursive: true });
  fs.writeFileSync(path.join(loserTemp, 'CodeIndex.Server.dll'), 'loser-bytes');
  fs.writeFileSync(path.join(loserTemp, srv.VERIFIED_MARKER_NAME), hex);

  assert.doesNotThrow(() => srv.publishCacheDir(loserTemp, finalDir, hex, '0.2.0'));
  // The winner's content is what's actually installed — a partial/duplicate
  // write from the loser never clobbers a valid, already-published cache dir.
  assert.equal(fs.readFileSync(path.join(finalDir, 'CodeIndex.Server.dll'), 'utf8'), 'winner-bytes');
});

test('publishCacheDir: a genuinely broken rename (not a race) still throws', (t) => {
  const root = mkTempDir(t, 'code-index-realfail-');
  const finalDir = path.join(root, 'nested', 'does', 'not', 'exist', '0.2.0');
  const hex = 'f'.repeat(64);
  const temp = path.join(root, '.tmp-install-x');
  fs.mkdirSync(temp, { recursive: true });
  // finalDir's parent doesn't exist and isCacheReady(finalDir) will be
  // false (nothing there) — renameSync should fail with ENOENT and that
  // failure should propagate instead of being swallowed as "someone else
  // already installed it".
  assert.throws(() => srv.publishCacheDir(temp, finalDir, hex, '0.2.0'));
});

test('parseTarBuffer + extractTarGz round-trip a small synthetic archive built the way GNU tar would', () => {
  const files = {
    'CodeIndex.Server.dll': Buffer.from('pretend-dll-bytes-01234567890'),
    'appsettings.json': Buffer.from(JSON.stringify({ Embedding: { Model: 'qwen3-embedding:4b' } })),
    'nested/sub/file.txt': Buffer.from('nested file content'),
  };

  const tarBuf = buildMinimalTar(files);
  const gz = zlib.gzipSync(tarBuf);

  const entries = srv.extractTarGz(gz);
  const byName = Object.fromEntries(entries.map((e) => [e.name, e.data]));

  assert.equal(entries.length, Object.keys(files).length);
  for (const [name, content] of Object.entries(files)) {
    assert.ok(byName[name], `missing entry ${name}`);
    assert.equal(Buffer.compare(Buffer.from(byName[name]), content), 0, `content mismatch for ${name}`);
  }
});

test('extractTarGz rejects a corrupted (non-gzip) buffer with a clear error, not a crash', () => {
  assert.throws(() => srv.extractTarGz(Buffer.from('not gzip data at all')), /not valid gzip/);
});

test('extractEntriesTo (zip-slip) refuses a hostile archive whose entry escapes destDir via ../ traversal', (t) => {
  // Build a real tar.gz — the same code path a hostile release asset would
  // take through downloadAssetBuffer -> extractTarGz -> extractEntriesTo —
  // rather than hand-constructing an entries array, so this exercises the
  // actual bytes-to-disk pipeline, not just the guard function in isolation.
  const dir = mkTempDir(t, 'code-index-zipslip-archive-');
  const destDir = path.join(dir, 'dest');
  fs.mkdirSync(destDir, { recursive: true });

  const files = { '../evil.txt': Buffer.from('pwned') };
  const gz = zlib.gzipSync(buildMinimalTar(files));

  const entries = srv.extractTarGz(gz);
  assert.throws(() => srv.extractEntriesTo(entries, destDir), /unsafe entry path/);
  assert.equal(fs.existsSync(path.join(dir, 'evil.txt')), false, 'traversal entry must not land outside destDir');
  assert.equal(fs.existsSync(path.join(destDir, '..', 'evil.txt')), false);
});

test('extractEntriesTo (zip-slip) refuses an absolute entry path', (t) => {
  const dir = mkTempDir(t, 'code-index-zipslip-abs-');
  const destDir = path.join(dir, 'dest');
  fs.mkdirSync(destDir, { recursive: true });
  const outsideTarget = path.join(dir, 'outside-abs.txt');

  assert.throws(
    () => srv.extractEntriesTo([{ name: outsideTarget, data: Buffer.from('pwned') }], destDir),
    /unsafe entry path/,
  );
  assert.equal(fs.existsSync(outsideTarget), false);
});

test('extractEntriesTo still writes legitimate nested entries inside destDir', (t) => {
  const dir = mkTempDir(t, 'code-index-zipslip-ok-');
  const destDir = path.join(dir, 'dest');
  fs.mkdirSync(destDir, { recursive: true });

  srv.extractEntriesTo(
    [
      { name: 'CodeIndex.Server.dll', data: Buffer.from('dll-bytes') },
      { name: 'nested/sub/file.txt', data: Buffer.from('nested content') },
    ],
    destDir,
  );

  assert.equal(fs.readFileSync(path.join(destDir, 'CodeIndex.Server.dll'), 'utf8'), 'dll-bytes');
  assert.equal(fs.readFileSync(path.join(destDir, 'nested', 'sub', 'file.txt'), 'utf8'), 'nested content');
});

test('parseTarBuffer already drops symlink entries, so extractEntriesTo never sees a symlink escaping destDir', () => {
  // typeFlag '2' is a symlink per the tar spec; parseTarBuffer only pushes
  // typeFlag '0'/'\0' (regular file) entries into its result — this pins
  // down that a symlink entry pointing outside the archive (e.g. linking
  // "safe-name" to "../../etc") can never reach extractEntriesTo at all.
  const header = buildTarHeader('escape-link', 0, { typeFlag: '2', linkname: '../../outside' });
  const tarBuf = Buffer.concat([header, Buffer.alloc(1024)]);
  const gz = zlib.gzipSync(tarBuf);

  const entries = srv.extractTarGz(gz);
  assert.equal(entries.length, 0, 'symlink entry must not be surfaced as an extractable entry');
});

test('every failure path in ensureServerInstalled/runServer throws LauncherExit rather than calling process.exit() directly', () => {
  // Regression test: on this Node/Windows combination, calling
  // process.exit() shortly after a fetch() call in the same process
  // reliably crashes with a libuv assertion (verified against a live
  // GitHub API call — see the LauncherExit class comment and CHANGELOG for
  // the repro). Every throw site that can run after ensureServerInstalled
  // has made a network call must go through LauncherExit instead, so
  // main()'s single catch can set process.exitCode and return without ever
  // calling process.exit() itself. This can't run those code paths without
  // a real cache/manifest/network fixture (covered by the manual
  // integration checks instead), but it does pin down the invariant that
  // matters: no raw `process.exit(` call sites exist outside runServer's
  // child-process event handlers, where it's registered signal listeners
  // (not a recent fetch) that make an explicit exit necessary.
  const source = fs.readFileSync(path.join(__dirname, 'server.js'), 'utf8');
  const isCommentLine = (line) => line.startsWith('//') || line.startsWith('*') || line.startsWith('/**');
  const exitCallLines = source
    .split('\n')
    .map((line, i) => ({ line: line.trim(), n: i + 1 }))
    .filter(({ line }) => /\bprocess\.exit\(/.test(line) && !isCommentLine(line));

  const allowedContext = ["process.exit(code ?? 0);", "process.exit(3);"];
  const unexpected = exitCallLines.filter(({ line }) => !allowedContext.includes(line));

  assert.deepEqual(
    unexpected,
    [],
    `unexpected process.exit() call site(s) outside the child-process event handlers: ${JSON.stringify(unexpected)}`,
  );
  assert.equal(exitCallLines.length, 2, 'expected exactly the two child.on(exit)/child.on(error) call sites');
});

test('a real CI-shaped archive (this repo\'s own build, packaged the way the workflow does) parses correctly and its checksum matches sha256sum', { skip: !process.env.CODEINDEX_TEST_ARCHIVE }, () => {
  const archivePath = process.env.CODEINDEX_TEST_ARCHIVE;
  const buffer = fs.readFileSync(archivePath);
  const entries = srv.extractTarGz(buffer);
  assert.ok(entries.length > 10, 'expected a real published server directory, got too few entries');
  assert.ok(entries.some((e) => e.name === 'CodeIndex.Server.dll'), 'CodeIndex.Server.dll must be present');
  assert.ok(entries.some((e) => e.name === 'appsettings.json'), 'appsettings.json must be present');

  const hex = crypto.createHash('sha256').update(buffer).digest('hex');
  if (process.env.CODEINDEX_TEST_ARCHIVE_SHA256) {
    assert.equal(hex, process.env.CODEINDEX_TEST_ARCHIVE_SHA256.toLowerCase());
  }
});

test('stripEmptyOptionalOverrides removes only the three declared overrides when set to the empty string', () => {
  const source = {
    CODEINDEX_CONFIG_FILE: '',
    CODEINDEX_Embedding__Endpoint: '',
    CODEINDEX_Embedding__Model: '',
    // Not one of the three names .mcp.json declares, and documented to treat "" as meaningful
    // (Embedding:QueryInstruction — "no prefix") — must survive untouched.
    CODEINDEX_Embedding__QueryInstruction: '',
    // Some other CODEINDEX_* variable this function was never asked to look at.
    CODEINDEX_CodeIndex__Projects__0__Root: '',
    PATH: '/usr/bin',
  };

  const result = srv.stripEmptyOptionalOverrides(source);

  assert.equal('CODEINDEX_CONFIG_FILE' in result, false);
  assert.equal('CODEINDEX_Embedding__Endpoint' in result, false);
  assert.equal('CODEINDEX_Embedding__Model' in result, false);
  assert.equal(result.CODEINDEX_Embedding__QueryInstruction, '');
  assert.equal(result.CODEINDEX_CodeIndex__Projects__0__Root, '');
  assert.equal(result.PATH, '/usr/bin');
  assert.equal(source.CODEINDEX_CONFIG_FILE, '', 'the input object itself must not be mutated');
});

test('stripEmptyOptionalOverrides leaves a genuine (non-empty) override untouched', () => {
  const source = {
    CODEINDEX_Embedding__Endpoint: 'http://example.internal:9999',
    CODEINDEX_Embedding__Model: 'nomic-embed-text',
    CODEINDEX_CONFIG_FILE: 'C:\\custom\\config.json',
  };

  const result = srv.stripEmptyOptionalOverrides(source);

  assert.equal(result.CODEINDEX_Embedding__Endpoint, 'http://example.internal:9999');
  assert.equal(result.CODEINDEX_Embedding__Model, 'nomic-embed-text');
  assert.equal(result.CODEINDEX_CONFIG_FILE, 'C:\\custom\\config.json');
});

test("OPTIONAL_ENV_OVERRIDES matches exactly the placeholders declared in .mcp.json's env block", () => {
  // Ties the stripped name list to the actual manifest, so a future .mcp.json edit that adds
  // another `${VAR}` optional-override placeholder without updating this list fails loudly here
  // instead of silently reintroducing the same empty-string bug for the new variable.
  const mcpJson = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '.mcp.json'), 'utf8'));
  const declared = Object.keys(mcpJson.mcpServers['code-index'].env);

  assert.deepEqual([...srv.OPTIONAL_ENV_OVERRIDES].sort(), declared.sort());
});

test('buildChildEnv: empty launcher-level overrides fall back to the config-file-derived value, never to ""', (t) => {
  const dir = mkTempDir(t, 'code-index-buildenv-fallback-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: '9.9.9' }));

  const configPath = path.join(dir, 'config.json');
  fs.writeFileSync(configPath, JSON.stringify({ Embedding: { Endpoint: 'http://from-config-file:11434' } }));

  withTempEnv(t, {
    CODEINDEX_CONFIG_FILE: configPath,
    // Exactly the shape Claude Code produces for two never-customized `${VAR}` placeholders.
    CODEINDEX_Embedding__Endpoint: '',
    CODEINDEX_Embedding__Model: '',
  });

  const isolated = requireIsolatedServerJs(t, binDir);
  const env = isolated.buildChildEnv();

  assert.equal(
    env.CODEINDEX_Embedding__Endpoint,
    'http://from-config-file:11434',
    'an empty launcher-level override must not shadow the config-file-derived value',
  );
  assert.equal(
    'CODEINDEX_Embedding__Model' in env,
    false,
    'an empty override with no config-file-derived counterpart must be absent entirely, not ""',
  );
});

test('buildChildEnv: a genuine (non-empty) override still wins over the config-file-derived value', (t) => {
  const dir = mkTempDir(t, 'code-index-buildenv-real-override-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: '9.9.9' }));

  const configPath = path.join(dir, 'config.json');
  fs.writeFileSync(configPath, JSON.stringify({ Embedding: { Endpoint: 'http://from-config-file:11434' } }));

  withTempEnv(t, {
    CODEINDEX_CONFIG_FILE: configPath,
    CODEINDEX_Embedding__Endpoint: 'http://user-set-this-deliberately:4321',
  });

  const isolated = requireIsolatedServerJs(t, binDir);
  const env = isolated.buildChildEnv();

  assert.equal(env.CODEINDEX_Embedding__Endpoint, 'http://user-set-this-deliberately:4321');
});

test('buildChildEnv: CODEINDEX_CONFIG_FILE="" resolves to the default config path, not an empty path', (t) => {
  const dir = mkTempDir(t, 'code-index-buildenv-defaultconfig-');
  const binDir = path.join(dir, 'bin');
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(path.join(dir, '.claude-plugin'), { recursive: true });
  fs.writeFileSync(path.join(dir, '.claude-plugin', 'plugin.json'), JSON.stringify({ serverVersion: '9.9.9' }));

  withTempEnv(t, { CODEINDEX_CONFIG_FILE: '' });

  const isolated = requireIsolatedServerJs(t, binDir);

  // `"" || DEFAULT_CONFIG_PATH` is truthy-fallback in plain JS, so this already resolved
  // correctly before the launcher fix too — pinned down here as a regression guard, and to
  // document that CODEINDEX_CONFIG_FILE="" is not a second, separate failure the way the
  // Embedding:Endpoint/Model shape is.
  assert.equal(isolated.CONFIG_PATH, path.join(os.homedir(), '.code-index-mcp', 'config.json'));
  // Must not throw despite there being no real config.json at that default path.
  assert.doesNotThrow(() => isolated.buildChildEnv());
});

// ── helpers ───────────────────────────────────────────────────────────────

/** Sets each key in `vars` on `process.env` for the duration of test `t`, restoring the prior
 * value (or deleting the key if it was unset) via `t.after`. `CONFIG_PATH`/`buildChildEnv` in the
 * module under test read `process.env` directly (there is no dependency-injection seam for it
 * here), so tests that need a specific `CODEINDEX_*` shape mutate the real global and must clean
 * up afterwards rather than leaking state into unrelated tests in this same process. */
function withTempEnv(t, vars) {
  const originals = {};
  for (const key of Object.keys(vars)) {
    originals[key] = process.env[key];
    process.env[key] = vars[key];
  }
  t.after(() => {
    for (const key of Object.keys(vars)) {
      if (originals[key] === undefined) delete process.env[key];
      else process.env[key] = originals[key];
    }
  });
}

/** Copies server.js into `binDir` (alongside the throwaway `.claude-plugin/plugin.json` the
 * caller already created) and requires that copy fresh — the same technique the
 * readExpectedChecksum tests above use. Needed anywhere a test wants to observe module-load-time
 * state (`CONFIG_PATH`, computed once from `process.env.CODEINDEX_CONFIG_FILE` at require time)
 * under environment variables the test itself controls, rather than whatever was set when this
 * test file's own top-level `require('./server.js')` first ran. */
function requireIsolatedServerJs(t, binDir) {
  const serverJsCopy = path.join(binDir, 'server.js');
  fs.copyFileSync(path.join(__dirname, 'server.js'), serverJsCopy);
  delete require.cache[require.resolve(serverJsCopy)];
  t.after(() => delete require.cache[require.resolve(serverJsCopy)]);
  return require(serverJsCopy);
}

/** Builds a minimal valid ustar archive (regular files only, short names) —
 * just enough structure to exercise parseTarBuffer without shelling out to
 * `tar`, so this test suite has zero external tool dependencies. */
function buildMinimalTar(files) {
  const blocks = [];
  for (const [name, data] of Object.entries(files)) {
    blocks.push(buildTarHeader(name, data.length));
    blocks.push(data);
    const pad = (512 - (data.length % 512)) % 512;
    if (pad > 0) blocks.push(Buffer.alloc(pad));
  }
  blocks.push(Buffer.alloc(1024)); // two zero-filled end-of-archive blocks
  return Buffer.concat(blocks);
}

function buildTarHeader(name, size, { typeFlag = '0', linkname = '' } = {}) {
  const header = Buffer.alloc(512);
  header.write(name, 0, 100, 'utf8');
  header.write('0000644\0', 100, 8, 'utf8'); // mode
  header.write('0000000\0', 108, 8, 'utf8'); // uid
  header.write('0000000\0', 116, 8, 'utf8'); // gid
  header.write(size.toString(8).padStart(11, '0') + '\0', 124, 12, 'utf8'); // size (octal)
  header.write('00000000000\0', 136, 12, 'utf8'); // mtime
  header.write('        ', 148, 8, 'utf8'); // checksum placeholder (spaces)
  header.write(typeFlag, 156, 1, 'utf8'); // typeflag: '0' regular file, '2' symlink, ...
  if (linkname) header.write(linkname, 157, 100, 'utf8'); // linkname (symlink/hardlink target)
  header.write('ustar\0', 257, 6, 'utf8'); // magic
  header.write('00', 263, 2, 'utf8'); // version

  let sum = 0;
  for (let i = 0; i < 512; i++) sum += header[i];
  header.write(sum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'utf8');

  return header;
}
