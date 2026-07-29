# code-index changelog

All notable changes to this plugin are listed here. Newest at the top.

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
