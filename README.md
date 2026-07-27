# code-index-mcp

A local MCP (Model Context Protocol) server that gives Claude Code semantic search over a
C# codebase, backed by a vector index built from locally computed embeddings. It exposes four
tools over stdio: `code_search`, `code_get_chunk`, `code_index_status`, and `code_reindex`.

## The problem this solves

Finding "where is X implemented" in a large C# codebase with `Grep`/`Glob` usually means several
broad text sweeps, each returning dozens of false-positive matches, followed by reading files
blind just to check whether they're the right one. Every file opened this way stays in the
session's context for the rest of the conversation and gets re-sent on every subsequent request —
it is the single biggest driver of wasted input tokens on a search-heavy session.

`code-index-mcp` replaces that first phase with semantic search: the codebase is chunked at the
level of individual class/interface/method/property/etc. declarations (via Roslyn), each chunk is
embedded once, and `code_search` fuses semantic similarity with exact symbol matching to return a
short, ranked list of the declarations that actually matter — not every line that happens to
contain a string. The index refreshes itself incrementally before every search, so results always
reflect the current state of the tree.

This only saves *search*. If a task genuinely requires reading eight files in full, they still
have to be read; output tokens are unaffected. On search-heavy sessions the expected saving is
roughly a third to a half of input tokens, plus fewer wasted iterations chasing the wrong file.

## Requirements

- **.NET 10 SDK** (`net10.0`, see `global.json`)
- **[Ollama](https://ollama.com/)**, running locally (`ollama serve`)
- The **`qwen3-embedding:4b`** model pulled into Ollama — about **2.5 GB** to download once
  (`ollama pull qwen3-embedding:4b`)
- **~10 GB of free VRAM** while the model is resident (see [Known limitations](#known-limitations)
  for what "resident" costs you on a card with less headroom)

Only `.cs` files are indexed. There is no support for other languages or file types.

## Setup

1. **Install Ollama** and make sure it's running:
   ```bash
   ollama serve
   ```
2. **Pull the embedding model:**
   ```bash
   ollama pull qwen3-embedding:4b
   ```
3. **Clone this repository** and build it:
   ```bash
   git clone <this-repo-url> code-index-mcp
   cd code-index-mcp
   dotnet build -c Release
   ```
4. **Point it at the project you want indexed** — edit
   `src/CodeIndex.Server/appsettings.json`:
   ```json
   {
     "CodeIndex": {
       "ProjectId": "myproject",
       "ProjectRoot": "/path/to/MyProject"
     },
     "Embedding": {
       "Endpoint": "http://localhost:11434",
       "Model": "qwen3-embedding:4b",
       "Dimensions": 1024,
       "KeepAlive": "30m",
       "QueryInstruction": "Given a developer's question about a codebase, retrieve the C# code that implements it."
     }
   }
   ```
   `QueryInstruction` is prepended to the query only (never to indexed chunk text) before it is
   embedded — see [Search quality](#search-quality-the-query-instruction-prefix) below for why,
   and note that changing it does **not** require rebuilding the index.

   `ProjectId` is the cache key — it is deliberately **not** derived from `ProjectRoot`, so the
   same project can live at different paths on different machines and still share a cache (see
   [Moving the cache between machines](#moving-the-cache-between-machines)).

   Instead of editing `appsettings.json` directly (and risking committing your local path), copy
   `src/CodeIndex.Server/appsettings.Local.json.example` to
   `src/CodeIndex.Server/appsettings.Local.json` and put your real `ProjectRoot`/`ProjectId` there.
   It is loaded after `appsettings.json` and is gitignored, so it never gets committed.
5. **Build the initial index:**
   ```bash
   dotnet run --project src/CodeIndex.Server -c Release -- --build-only
   ```
   This is a one-time, from-scratch build. Expect several minutes for a few hundred files (see
   the measured numbers below) — every chunk has to be embedded once. Subsequent runs of the
   server refresh incrementally instead of repeating this.
6. **Check it worked:**
   ```bash
   dotnet run --project src/CodeIndex.Server -c Release -- --status
   ```
   This prints file/chunk counts, cache location and size, and measures a live refresh + search
   round trip.
7. **Register the server with Claude Code** — see [Registering with Claude Code](#registering-with-claude-code) below.

## The four tools

### `code_search`

Semantic + symbol search over the indexed source. Prefer this over `Grep` whenever the goal is to
find where something is *implemented*, not to find every literal occurrence of a string.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `query` | string | — | Natural-language question or exact identifier/symbol name. |
| `limit` | int | `10` | Maximum number of hits to return. |
| `kind` | string? | `null` | Restrict to one chunk kind: `Class`, `Interface`, `Struct`, `Record`, `Enum`, `Method`, `Constructor`, `Property`, `Field`, or `FileFragment`. Case-insensitive; an unrecognized value is ignored silently. |
| `path_filter` | string? | `null` | Case-insensitive substring filter on the file's relative path. |

Returns a ranked list of hits, each with an `id`, file path, line range, kind, symbol,
signature, doc comment (if any), and a short excerpt. The index refreshes incrementally before
every call.

### `code_get_chunk`

Fetches the full body of one chunk by the `id` returned from a `code_search` hit, for when the
excerpt isn't enough.

| Parameter | Type | Description |
|---|---|---|
| `id` | int | Chunk id from a `code_search` hit's `id` field. |

Chunk ids are ordinal positions in the index **as it existed at that specific search** — they do
not survive a reindex. Always take the id from the most recent `code_search` result.

### `code_index_status`

No parameters. Reports how many files/chunks are indexed, which embedding model and
dimensionality built it, when it was last built, and where the on-disk cache lives. Useful to
check whether the index is warmed up, or to diagnose stale/incomplete-looking results.

### `code_reindex`

No parameters. Forces a full rebuild from scratch — every file is re-chunked and re-embedded, not
just what changed. `code_search` already refreshes incrementally before every call, so this is
only needed when the index seems wrong in a way incremental refresh should already have caught
(e.g. after changing the embedding model, or recovering from a corrupted cache). Slower than a
normal search.

## Measured characteristics

Measured against a 723-file C# SDK (`qwen3-embedding:4b`, 1024 dimensions):

| Metric | Measured |
|---|---|
| Files indexed | 723 |
| Chunks produced | 8,735 |
| Initial index build (from scratch) | 451.6 s (~7.5 min) |
| Cache size on disk | 36.7 MB |
| Incremental refresh (no changes) | 0.16 s |
| Query, model resident (warm) | ~200 ms (~190 ms embedding the query + ~1.6 ms search) |
| Query, model not resident (cold) | ~12 s |

The warm number is not a design estimate — an earlier estimate of "under 100 ms" turned out to be
wrong because it only accounted for the vector search itself and ignored that every query must
first get an embedding back from Ollama, which dominates the cost. See
`docs/superpowers/specs/2026-07-27-code-index-mcp-design.md` section 12 for the full comparison
between the original estimates and what was actually measured.

The cold number is why `Embedding:KeepAlive` exists — see the next section.

## Search quality: the query instruction prefix

Qwen3-Embedding (like other E5/GTE-family models) is trained asymmetrically: passage/chunk text
is encoded as-is, but a query is expected to carry a short task-instruction prefix, formatted as
`Instruct: {task}\nQuery: {query}`. Embedding the query exactly like a passage — which is what
this project did before this was measured — throws away part of what the model was actually
trained to use.

`Embedding:QueryInstruction` in `appsettings.json` controls this prefix. It applies **only** to
the query side of a search (`IEmbeddingClient.EmbedQueryAsync`) — chunk/passage text is never
touched, so **changing it does not require a reindex**, unlike `Model` or `Dimensions`, which
change what is actually stored in `vectors.bin`. Set it to `null` or an empty string to send the
bare query with no prefix.

Measured on the same seven natural-language queries used to validate the design, against
the same 723-file C# SDK, each result verified by reading the code at the reported location
rather than trusting a plausible-looking symbol name:

| Query | Without prefix | With prefix (current default) |
|---|---|---|
| converting drops to XRP | hit, rank 1 | hit, rank 1 |
| parsing an account address from a string | hit, rank 1 | hit, rank 1 |
| computing a transaction hash | hit, rank 1 | hit, rank 1 |
| where are trust lines validated | hit, rank 2 | hit, rank 1 |
| retry logic for failed requests | adjacent only | hit, rank 1 |
| websocket connection lifecycle | adjacent, poorly ranked | hit, rank 1 |
| how is a payment transaction signed | **miss** | hit, rank 3 — reported honestly, not rounded up: rank 1 is an integration test that exercises the same signing call, rank 2 is unrelated; the actual implementation, `XrplWallet.ComputeSignature`, is rank 3 |

**4 of 7 correct in the top 3 without the prefix → 7 of 7 hits with it, 6 of them rank 1.** This
was a measured decision, not a stylistic one. Two more expensive follow-ups were considered and
deliberately not applied, because the prefix alone closed the gap: raising `Dimensions` to the
model's native 2560 (requires a reindex, cache grows to ~92 MB), and switching to
`qwen3-embedding:8b` (another 2.5 GB download, plus a reindex).

## Cold start and `KeepAlive`

Ollama unloads a model from VRAM after 5 minutes of inactivity by default. At a realistic search
cadence (a query every several minutes, not every few seconds), that default would mean paying the
~12 s reload on effectively every query — slower than `Grep`, and enough to erase the entire point
of this project.

`Embedding:KeepAlive` in `appsettings.json` (default `"30m"`) is sent as Ollama's `keep_alive`
field on every `api/embed` request, refreshing how long the model stays resident on each call.
`"30m"` covers a normal working session's gaps between searches without permanently pinning
10 GB of VRAM the way `"-1"` (keep loaded forever) would — on a 16 GB card that would starve
everything else running on the GPU. Set it to `"-1"` only if you have VRAM to spare and want to
eliminate the cold-start cost entirely for a long-running session.

## Pointing it at a different repository

Edit `CodeIndex:ProjectRoot` and `CodeIndex:ProjectId` in `src/CodeIndex.Server/appsettings.json`,
or override them without touching the file via environment variables (the `CODEINDEX_` prefix,
`__` as the section separator):

```bash
export CODEINDEX_CodeIndex__ProjectRoot="/path/to/OtherProject"
export CODEINDEX_CodeIndex__ProjectId="otherproject"
```

Each distinct `ProjectId` gets its own cache directory under
`%LocalAppData%/code-index-mcp/<ProjectId>` (or wherever `CodeIndex:CacheDirectory` points, if
set explicitly) — indexing a second project never touches the first project's cache. Only one
project is indexed per running server instance; indexing several projects at once is out of
scope.

## Moving the cache between machines

The on-disk cache (`manifest.json` + `vectors.bin`, ~37 MB for this project's size) can simply be
copied to `%LocalAppData%/code-index-mcp/<ProjectId>` on another machine, even if the project
lives under a different drive letter or path there. This works because of three decisions, each
already made for other reasons:

1. **Paths inside the manifest are relative** to the project root — the cache never embeds
   an absolute path or drive letter.
2. **The cache key is `ProjectId`, not a hash of the project path** — the same key applies on both
   machines regardless of where the repository actually sits.
3. **Freshness checks fall back to a content hash** when size/timestamp differ — after a
   `git clone` or `git checkout`, every file's modification time changes but content usually
   doesn't, so copying the cache (or just switching branches) doesn't trigger a needless full
   reindex.

There's no automated sync between machines — the source is already synced via git, and the
benefit of automating a 37 MB copy or a several-minute rebuild is smaller than the cost of
building and maintaining that automation.

## Registering with Claude Code

Register the server using the **built Release binary**, not `dotnet run` — `dotnet run` re-checks
whether a build is needed on every launch, which adds a delay to every server start for no
benefit once the binary is built:

```bash
claude mcp add code-index -- "<repo>/src/CodeIndex.Server/bin/Release/net10.0/CodeIndex.Server.exe"
```

Substitute `<repo>` with the actual absolute path to wherever you cloned and built this
repository. Rebuild
(`dotnet build -c Release`) after pulling changes to this repo; the registration itself does not
need to change.

## Untrusted content

Every source fragment `code_search` and `code_get_chunk` return is wrapped in
`<untrusted-content origin="...">...</untrusted-content>` markers. The indexed project's source
is still, ultimately, third-party text as far as the agent reading it is concerned — a comment or
string literal inside it could contain text crafted to look like an instruction ("ignore previous
instructions and...") aimed at whatever agent later reads the search result. The markers tell a
downstream agent that follows the standard `untrusted-content` convention to treat everything
between them as data to read, never as instructions to follow, regardless of what it appears to
say. Any literal closing-tag substring inside the indexed content is defused (a zero-width space
is inserted) so indexed source can never forge its own closing marker and escape the wrapper.

## Known limitations

- **Cold-start cost.** See [Cold start and `KeepAlive`](#cold-start-and-keepalive) above — even
  with `KeepAlive` set, the *first* query after the model has actually unloaded still pays the
  full ~12 s reload.
- **Not every file gets structural chunks.** Of the 723 files in the measured run, 11 produced no
  chunks from the Roslyn-based chunker (e.g. syntax that Roslyn can't decompose meaningfully) and
  fell back to plain line-window chunking for that file instead of per-declaration chunks.
- **Chunk ids do not survive a reindex.** They are ordinal positions in the specific index
  snapshot a `code_search` call ran against. An explicit `code_reindex`, or even an automatic
  incremental refresh that added/removed/reordered chunks anywhere in file order, invalidates
  every id from a previous search. Always fetch a fresh id via `code_search` immediately before
  calling `code_get_chunk`.
- **A file edit that preserves both size and timestamp is invisible to incremental refresh.**
  Freshness is checked cheaply first by comparing file length and last-write-time against what
  was last indexed, and only falls back to a full content hash when one of those differs. An edit
  that happens to leave both unchanged (contrived, but possible with tooling that rewrites a file
  in place without updating its mtime, or with mtime resolution/clock skew) will not be picked up
  until the next explicit `code_reindex`.

## Development

TDD throughout; see `docs/superpowers/specs/2026-07-27-code-index-mcp-design.md` for the full
design (in Russian) and `docs/superpowers/plans/2026-07-27-code-index-mcp.md` for the
implementation plan. Run the test suite with:

```bash
dotnet test
```
