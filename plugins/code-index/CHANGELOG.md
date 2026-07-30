# code-index changelog

All notable changes to this plugin are listed here. Newest at the top.

## v0.2.1 — 2026-07-30

### Fixes
- **the plugin failed to start for every user who never manually set one of its three optional
  environment overrides — in practice, everyone.** `.mcp.json` declares
  `CODEINDEX_CONFIG_FILE`, `CODEINDEX_Embedding__Endpoint`, and `CODEINDEX_Embedding__Model` via
  `${VAR}` placeholders. When a placeholder's variable is not set in the host environment, Claude
  Code substitutes an empty string instead of omitting the key, so the server always received
  `CODEINDEX_Embedding__Endpoint=""`. .NET configuration binds that as an explicit override,
  clobbering `EmbeddingOptions.Endpoint`'s compiled-in default of `http://localhost:11434`, which
  then reached `new Uri("")` and crashed with an unhelpful `Invalid URI: The URI is empty.` before
  the server ever served a tool. `bin/server.js` now strips exactly these three variables from the
  child environment when (and only when) their value is the empty string, so an unset override
  behaves as unset — falling through to a `~/.code-index-mcp/config.json`-derived value when there
  is one, or to the server's own default otherwise — instead of shadowing either with `""`. A
  genuine override (any non-empty value, for any of the three) is untouched. `CODEINDEX_Embedding__QueryInstruction`
  and every other setting are deliberately left alone: only the exact three names `.mcp.json`
  declares are stripped, not a blanket "every empty `CODEINDEX_*` variable" rule, because
  `QueryInstruction=""` is documented, load-bearing configuration ("no prefix"), not an
  accidentally-empty override.
- `CODEINDEX_CONFIG_FILE=""` was checked separately and is **not** a second failure: the
  launcher's own `process.env.CODEINDEX_CONFIG_FILE || DEFAULT_CONFIG_PATH` already falls back to
  the default config path for an empty string (plain JS `||` treats `""` as falsy), so this one was
  already resolving correctly. It is still covered by the fix above (and by a regression test)
  purely for consistency — an empty override no longer leaks through to the child process at all,
  for any of the three variables.
- added `EmbeddingOptions.Validate()` (`CodeIndex.Core`), called before the embedding `HttpClient`
  is constructed in `Program.cs`, as a second, independent line of defense: anyone who runs
  `CodeIndex.Server` directly (bypassing this launcher and its new stripping) with an empty
  `Embedding:Endpoint` or `Embedding:Model` now gets a message naming the exact setting instead of
  the same unhelpful URI-parse exception. This is validation with a clear error, not a silent
  fallback — a genuinely misconfigured endpoint should still stop the server, just with an
  actionable message. Not accompanied by a `serverVersion` bump: the reported failure is fully
  fixed at the launcher level for every plugin-installed user, so the already-published
  `server-v0.2.0` release keeps serving them unchanged; this hardening reaches anyone running the
  server directly the next time `serverVersion` moves for an unrelated reason.

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
## v0.1.3 — 2026-07-29

### Documentation
- benchmarked four lighter alternatives to the default `qwen3-embedding:4b`
  (`qwen3-embedding:0.6b`, `nomic-embed-text`, `mxbai-embed-large`,
  `all-minilm`) against the same corpus, so someone on weaker hardware can
  pick with numbers instead of guessing. Added a comparison table to the
  repository README (VRAM, download size, dimensions, index time, cache
  size, warm query latency, reference-query accuracy, and a re-derived
  `MinCosineSimilarity` per model) and pointed this plugin's Prerequisites
  section at it.

### Fixes
- `Embedding:QueryInstruction` was hardcoded into an
  `"Instruct: {QueryInstruction}\nQuery: {query}"` template, so no
  configured value could ever produce the correct prefix for a model that
  does not use Qwen's instruction format (e.g. `nomic-embed-text`'s
  `"search_query: "`). It is now a raw prefix, prepended verbatim; the
  default value is unchanged in effect (produces the identical embedded
  string for the default model), but the setting can now actually be
  pointed at a different model family.

### Internal
- rebuilt the bundled `bin/server/` binary from source to pick up the
  `QueryInstruction` fix above.

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
