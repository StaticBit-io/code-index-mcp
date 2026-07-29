# code-index changelog

All notable changes to this plugin are listed here. Newest at the top.

## v0.1.0 — 2026-07-29

### Features
- initial plugin packaging of code-index-mcp: `bin/server.js` launcher with
  preflight checks (.NET 10 runtime, Ollama reachability, embedding model
  pulled, at least one project configured), portable framework-dependent
  `CodeIndex.Server` build committed under `bin/server/`, bundled
  `code-search` skill.
