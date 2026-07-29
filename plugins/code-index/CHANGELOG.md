# code-index changelog

All notable changes to this plugin are listed here. Newest at the top.

## v0.2.0 — 2026-07-29

### Changed
- **the published server is no longer committed to the repository.** `bin/server/` held a 14 MB,
  43-file publish output that doesn't delta-compress in git — every future rebuild permanently grew
  the repository, and it already outweighed the entire commit history. `bin/server.js` now fetches
  `code-index-server-<version>.tar.gz` from a GitHub Release on first use, verifies it against
  `bin/server.sha256` (committed in this repository — the repo remains the trust anchor even though
  the binary itself no longer is), extracts it into `~/.code-index-mcp/server/<version>/`, and runs
  from there. Every subsequent launch of the same version reuses the cache with no network access at
  all. See README's "How the server binary is fetched" for the full flow, the exact message for each
  failure path (offline with an empty cache, checksum mismatch, missing release, private-repo auth),
  and how concurrent installs (two Claude Code windows starting at once) are handled without a
  partially-downloaded file ever being mistaken for a good one.
- the server release is now built by CI on `ubuntu-latest`
  (`.github/workflows/release-server.yml`) with `-p:SatelliteResourceLanguages=en`, rather than
  published from a maintainer's Windows machine — the previous committed build carried a small
  number of Windows-machine-specific Roslyn satellite assemblies that a portable build shouldn't.
- added a `serverVersion` field to the plugin manifest, deliberately separate from the plugin's own
  `version`: it names exactly which server release to fetch, so a docs/skill-only plugin bump
  doesn't force a redundant 14 MB re-download, and a server-only rebuild doesn't need a marketplace
  version bump to reach users.
- added `CODEINDEX_SERVER_DIR` as a development-only override: point it at any published
  `CodeIndex.Server` build directory to run it directly, skipping the download and checksum check
  entirely. Not for normal use.

## v0.1.2 — 2026-07-29

### Documentation
- the `code-search` skill only covered usage, leaving an agent with no way to
  recognize an unindexed project or offer to configure one. It now covers
  reading `code_index_status` for that signal, when offering is (and isn't)
  warranted given the ~0.6s/file indexing cost, how to add a project to
  `~/.code-index-mcp/config.json` (including `Id`'s validation rules and why
  environment variables can't register a project), and that the config file
  is user settings — never edit it without showing the change first.

## v0.1.1 — 2026-07-29

### Fixes
- a malformed `config.json` was reported as "No project is configured", sending
  the user to look for the wrong problem; parse failures now name the file and
  the underlying cause. Only a missing file is treated as "not configured yet".
- the launcher's own error message advised setting `CODEINDEX_CodeIndex__Projects__*`
  environment variables, which `.mcp.json` never declares and which therefore
  never reach the server. The advice was wrong in the message a user sees at the
  exact moment something is broken.
- fixed a broken `#setup` anchor in both READMEs and tagged fenced code blocks
  with their language.

## v0.1.0 — 2026-07-29

### Features
- initial plugin packaging of code-index-mcp: `bin/server.js` launcher with
  preflight checks (.NET 10 runtime, Ollama reachability, embedding model
  pulled, at least one project configured), portable framework-dependent
  `CodeIndex.Server` build committed under `bin/server/`, bundled
  `code-search` skill.
