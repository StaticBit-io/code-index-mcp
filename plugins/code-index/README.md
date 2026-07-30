> 🌐 **Language**: **English** | [Русский](README.ru.md)

# code-index plugin

Local stdio MCP that gives Claude Code semantic search over one or more C# codebases. Spawns
`CodeIndex.Server.dll` under **your** .NET 10 runtime; embeddings are computed by **your** local
Ollama instance (`qwen3-embedding:4b`) — no maintainer-owned server in the path, no code ever
leaves your machine except to `localhost:11434`.

Four tools: `code_search`, `code_get_chunk`, `code_index_status`, `code_reindex`. Full parameter
documentation and usage guidance lives in the bundled skill, `code-search` — read
[`skills/code-search/SKILL.md`](skills/code-search/SKILL.md) or just ask Claude to search your
code once the plugin is installed.

## Prerequisites

Unlike a plugin that talks to a public server, this one has real local prerequisites — the
launcher checks all of them before starting the server and tells you exactly what's missing
(see [Troubleshooting](#troubleshooting) below):

- **.NET 10 runtime** on PATH (`dotnet --list-runtimes` shows a `Microsoft.NETCore.App 10.x`
  line). Install: <https://dotnet.microsoft.com/download/dotnet/10.0>.
- **Ollama**, running locally (`ollama serve`).
- The **`qwen3-embedding:4b`** model pulled (`ollama pull qwen3-embedding:4b`, ~2.5 GB one-time
  download, **~10 GB of VRAM** resident while it runs). If that doesn't fit your GPU, `Embedding`
  is configurable (see [Configuration](#configuration) below) — the
  [repository README's model comparison](../../README.md#choosing-an-embedding-model-measured)
  measures four lighter alternatives on the same benchmark, including one (`all-minilm`) that
  costs under 30 MB of VRAM, so you can pick with numbers instead of guessing.
- **At least one project configured** — see [Configuration](#configuration) below. There is no
  usable default here: project paths are only known to you.

## Install

```text
/plugin marketplace add StaticBit-io/code-index-mcp
/plugin install code-index@code-index-mcp
```

(Or, for a local checkout: `/plugin marketplace add /path/to/code-index-mcp`.)

## Configuration

Create `~/.code-index-mcp/config.json` (Windows: `%USERPROFILE%\.code-index-mcp\config.json`)
with the project(s) you want indexed:

```json
{
  "CodeIndex": {
    "Projects": [
      { "Id": "myproject", "Root": "/path/to/MyProject" }
    ]
  }
}
```

This is the same shape as the server's own `CodeIndex`/`Embedding` configuration (see the
[repository README](../../README.md#manual-setup-build-from-source)) — `Id` is the cache key, `Root` is the absolute path
to the repository, and an optional `Extensions` list narrows or widens which files get indexed
per project. Add more entries to index several repositories from one server:

```json
{
  "CodeIndex": {
    "Projects": [
      { "Id": "myproject", "Root": "/path/to/MyProject" },
      { "Id": "otherproject", "Root": "/path/to/OtherProject", "Extensions": [".cs"] }
    ]
  },
  "Embedding": {
    "Endpoint": "http://localhost:11434",
    "Model": "qwen3-embedding:4b"
  }
}
```

`Embedding` is optional — omit it to use the defaults above.

### Why a file, not environment variables

Expressing several projects purely through environment variables means one clunky, indexed
variable per field:

```bash
CODEINDEX_CodeIndex__Projects__0__Id=myproject
CODEINDEX_CodeIndex__Projects__0__Root=/path/to/MyProject
```

— and Claude Code does not forward arbitrary host environment variables into a plugin's
subprocess; only the variables a plugin's `.mcp.json` explicitly declares are passed through (see
`env` in [`.mcp.json`](.mcp.json)). A dynamic, unbounded list like "one project per index" can't
be declared that way. A config file the launcher reads directly has no such limit and is the only
practical way to configure more than one project. It also means your local repository paths never
need to touch `.mcp.json`, an environment variable, or anything else Claude Code stores.

### Precedence

Three settings **are** declared in `.mcp.json` and can be overridden per the normal environment
rules of your OS/shell — `CODEINDEX_CONFIG_FILE` (point at a different config file location),
`CODEINDEX_Embedding__Endpoint`, and `CODEINDEX_Embedding__Model`. For every setting, an
environment variable already present when Claude Code launches the server wins over the same key
in the config file; the config file fills in anything not already set that way. In practice: use
the file for `Projects` (the part env vars are clumsy for), and env vars only if you specifically
need to override the Ollama endpoint or model without editing the file.

Restart Claude Code (or just retry your search) after creating or editing the config file.

## First search after install: this is expected to take minutes, not seconds

The very first time `code_search` (or `code_reindex`) runs against a project, there is no index
yet — every file has to be chunked and embedded from scratch. For a project of a few hundred
files this is **several minutes**, not a hang. `code_index_status` reports progress-relevant
numbers (file/chunk counts, last build time) once it completes; subsequent searches refresh
incrementally and are fast (sub-second to a few seconds). If Claude appears to sit quietly after
your first search on a freshly configured project, that is the index being built — let it finish
rather than assuming something is stuck.

## Verify it works

```text
/mcp
```

Should show `code-index: connected, 4 tools`. Then try:

```text
Search this codebase for where trustline deletion is validated.
```

The agent will pick `mcp__plugin_code-index_code-index__code_search`.

## Troubleshooting

The launcher (`bin/server.js`) checks the following before the server ever starts, and prints one
of these to stderr instead of a stack trace or a silent hang:

**No project configured:**
```text
[code-index] No project is configured — there is nothing to search yet.

[code-index] Create <path>\.code-index-mcp\config.json with at least one project:

  {
    "CodeIndex": {
      "Projects": [
        { "Id": "myproject", "Root": "C:\\path\\to\\MyProject" }
      ]
    }
  }

[code-index] Then restart Claude Code so the server picks up the new configuration.
```

**Ollama not running:**
```text
[code-index] Cannot reach Ollama at http://localhost:11434.

[code-index] code-index-mcp needs Ollama running locally to compute embeddings.
[code-index] Start it with:

  ollama serve

[code-index] Then ask your question again.
```

**Model not pulled:**
```text
[code-index] Ollama is running, but model 'qwen3-embedding:4b' is not pulled yet.

[code-index] Pull it (about 2.5 GB, one-time download):

  ollama pull qwen3-embedding:4b

[code-index] Then ask your question again.
```

**.NET 10 runtime missing** produces a similar message naming `dotnet --list-runtimes` and the
download link.

If the server actually starts but a *later* embedding call fails (e.g. Ollama was stopped mid
session), `code_search` degrades instead of erroring outright — see "The `warning` field" in the
skill for what a stale-index warning means and why the hits are still usable.

## Platforms

The bundled `bin/server/` build is a **portable, framework-dependent** publish of
`CodeIndex.Server` — no native/AOT dependencies anywhere in its dependency graph (pure managed
code: Roslyn, `System.Numerics.Tensors`, no SQLite or other native interop), so the same build
runs under `dotnet CodeIndex.Server.dll` on any OS with a matching .NET 10 runtime. This is
simpler than a per-RID binary matrix, with one caveat: this build was produced on Windows, and a
small number of satellite assemblies resolved by the .NET SDK at publish time are
platform-specific to the build machine (unused at runtime — this server only logs to
console/stderr). If you hit an issue running the committed build on Linux/macOS, republish it
locally: `dotnet publish src/CodeIndex.Server -c Release -o plugins/code-index/bin/server` from a
checkout of the [repository](../../).

## Privacy

- Nothing leaves your machine except outbound HTTP to your own Ollama instance (`localhost:11434`
  by default) and disk I/O under the project roots you configured and the on-disk index cache
  (`%LocalAppData%/code-index-mcp/<Id>` by default).
- The server process lives only as long as Claude Code keeps the stdio pipe open — terminates
  with the client.
