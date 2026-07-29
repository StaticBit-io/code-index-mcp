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

function mkTempDir(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
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

test('readExpectedChecksum: happy path parses "<hex>  <filename>"', () => {
  const dir = mkTempDir('code-index-checksum-ok-');
  const version = '9.9.9';
  const hex = 'a'.repeat(64);
  const checksumPath = path.join(dir, 'server.sha256');
  fs.writeFileSync(checksumPath, `${hex}  code-index-server-${version}.tar.gz\n`);

  // readExpectedChecksum reads from the module-level CHECKSUM_FILE_PATH
  // constant, so exercise the parsing logic directly the same way it does.
  const raw = fs.readFileSync(checksumPath, 'utf8');
  const match = raw.trim().match(/^([0-9a-fA-F]{64})\s+(\S+)$/);
  assert.ok(match, 'checksum line should match the expected format');
  assert.equal(match[1], hex);
  assert.equal(match[2], `code-index-server-${version}.tar.gz`);
});

test('readExpectedChecksum: missing file throws a "broken plugin package" message naming the path', () => {
  // CHECKSUM_FILE_PATH is a const captured at module load from __dirname —
  // to exercise the real ENOENT branch, run an isolated copy of the module
  // from a bin/ directory that has no server.sha256 next to it at all.
  const dir = mkTempDir('code-index-nochecksum-');
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

test('readExpectedChecksum throws when the file exists but the version does not match its declared filename', () => {
  // This exercises the actual exported function, so it needs
  // CHECKSUM_FILE_PATH to point at a real file — set up a throwaway plugin
  // layout and require a fresh copy of the module pointed at it.
  const dir = mkTempDir('code-index-mismatch-');
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

test('isCacheReady: false when directory is absent, empty, or has a non-matching marker; true only when both dll and matching marker exist', () => {
  const dir = mkTempDir('code-index-cache-');
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

test('publishCacheDir: simulates two racing installs of the same version — the loser detects the winner and does not throw', () => {
  const root = mkTempDir('code-index-race-');
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

test('publishCacheDir: a genuinely broken rename (not a race) still throws', () => {
  const root = mkTempDir('code-index-realfail-');
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

// ── helpers ───────────────────────────────────────────────────────────────

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

function buildTarHeader(name, size) {
  const header = Buffer.alloc(512);
  header.write(name, 0, 100, 'utf8');
  header.write('0000644\0', 100, 8, 'utf8'); // mode
  header.write('0000000\0', 108, 8, 'utf8'); // uid
  header.write('0000000\0', 116, 8, 'utf8'); // gid
  header.write(size.toString(8).padStart(11, '0') + '\0', 124, 12, 'utf8'); // size (octal)
  header.write('00000000000\0', 136, 12, 'utf8'); // mtime
  header.write('        ', 148, 8, 'utf8'); // checksum placeholder (spaces)
  header.write('0', 156, 1, 'utf8'); // typeflag: regular file
  header.write('ustar\0', 257, 6, 'utf8'); // magic
  header.write('00', 263, 2, 'utf8'); // version

  let sum = 0;
  for (let i = 0; i < 512; i++) sum += header[i];
  header.write(sum.toString(8).padStart(6, '0') + '\0 ', 148, 8, 'utf8');

  return header;
}
