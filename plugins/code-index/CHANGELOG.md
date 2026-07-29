# code-index changelog

All notable changes to this plugin are listed here. Newest at the top.

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
