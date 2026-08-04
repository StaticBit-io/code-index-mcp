# code-index-mcp

[![CI](https://github.com/StaticBit-io/code-index-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/StaticBit-io/code-index-mcp/actions/workflows/ci.yml)

A local MCP (Model Context Protocol) server that gives Claude Code semantic search over one or
more C# codebases, backed by a vector index built from locally computed embeddings. It exposes
four tools over stdio: `code_search`, `code_get_chunk`, `code_index_status`, and `code_reindex` —
each can target one configured project or, when `project` is omitted, every configured project at
once.

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

**It does not save tokens.** That claim was in this README, was reasoned from the mechanism rather
than measured, and an external reviewer correctly called it out. Four paired-agent measurements
across two corpora since then all point the same way: a competent `Grep`/`Glob`/`Read` agent is
cheaper — currently by roughly 10–15% — and finds slightly *more* of the relevant code. What this
tool reliably does is reach an answer in fewer searches, which is worth having when you have no
identifier to guess at, and is worth nothing when you do. The full numbers, the noise floor, and the
one task shape where it does win are in
[Cost and accuracy](#cost-and-accuracy-what-four-measurements-actually-showed) below.

## Requirements

- **.NET 10 SDK** (`net10.0`, see `global.json`)
- **[Ollama](https://ollama.com/)**, running locally (`ollama serve`)
- The **`qwen3-embedding:4b`** model pulled into Ollama — about **2.5 GB** to download once
  (`ollama pull qwen3-embedding:4b`)
- **~10 GB of free VRAM** while the model is resident (see [Known limitations](#known-limitations)
  for what "resident" costs you on a card with less headroom) — on weaker hardware, see
  [Choosing an embedding model](#choosing-an-embedding-model-measured) below for measured, lighter
  alternatives instead of guessing

By default, `.cs`, `.razor`, and `.md` files are indexed — see
[Which files get indexed](#which-files-get-indexed) below for how chunking differs by extension
and how to configure a different set per project.

## Install as a Claude Code plugin (recommended)

The easiest way to run this server is as a Claude Code plugin — a launcher that checks every
prerequisite above (`.NET` runtime, Ollama reachable, model pulled, at least one project
configured) and fails with an actionable message instead of a stack trace or a silent hang, then
fetches the matching prebuilt, portable server binary from a GitHub Release on first use (cached
locally after that — see
[How the server binary is fetched](plugins/code-index/README.md#how-the-server-binary-is-fetched)):

```text
/plugin marketplace add StaticBit-io/code-index-mcp
/plugin install code-index@code-index-mcp
```

Then create `~/.code-index-mcp/config.json` with the project(s) you want indexed:

```json
{
  "CodeIndex": {
    "Projects": [
      { "Id": "myproject", "Root": "/path/to/MyProject" }
    ]
  }
}
```

Full plugin documentation — configuration precedence, first-run expectations (the first search
against a freshly configured project takes minutes while the index builds from scratch), and the
exact troubleshooting messages for each missing prerequisite — lives in
[`plugins/code-index/README.md`](plugins/code-index/README.md)
([Русский](plugins/code-index/README.ru.md)).

Building a server locally to test the plugin against (needed only if you're contributing to this
repo, not for normal use): `dotnet publish src/CodeIndex.Server -c Release -o some/dir`, then point
the plugin at it with `CODEINDEX_SERVER_DIR=some/dir` — see
[the plugin README](plugins/code-index/README.md#how-the-server-binary-is-fetched). Cutting an
actual release (tagging `server-v<version>`, publishing the GitHub Release asset the plugin fetches
automatically) is handled by `.github/workflows/release-server.yml`.

## Manual setup (build from source)

Use this path if you'd rather run the server directly from a source checkout (development on
this repo, or a platform with no published release asset yet — see
[Platforms](plugins/code-index/README.md#platforms)) instead of installing the plugin above.

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
4. **Point it at the project(s) you want indexed** — edit
   `src/CodeIndex.Server/appsettings.json`:
   ```json
   {
     "CodeIndex": {
       "Projects": [
         { "Id": "myproject", "Root": "/path/to/MyProject" }
       ]
     },
     "Embedding": {
       "Endpoint": "http://localhost:11434",
       "Model": "qwen3-embedding:4b",
       "Dimensions": 1024,
       "KeepAlive": "30m",
       "QueryInstruction": "Instruct: Given a developer's question about a codebase, retrieve the C# code that implements it.\nQuery: ",
       "MinCosineSimilarity": 0.55
     }
   }
   ```
   `MinCosineSimilarity` is the vector branch's relevance floor — a chunk whose cosine similarity
   to the query falls below it is excluded outright, never returned just to pad out a short result
   set. `0.55` is this project's measured default for `qwen3-embedding:4b` at 1024 dimensions (see
   [Searching across projects](#searching-across-projects) for the measurement); re-measure before
   trusting it with a different model, dimensionality, or `QueryInstruction` — see
   [Choosing an embedding model](#choosing-an-embedding-model-measured) below for that measurement
   already done for four alternatives, including two that need far less VRAM than the default.
   `CodeIndex:Projects` is a list — a single project is just a one-element list, as above. To
   index several repositories from one server, add more entries:
   ```json
   {
     "CodeIndex": {
       "Projects": [
         { "Id": "myproject", "Root": "/path/to/MyProject" },
         { "Id": "otherproject", "Root": "/path/to/OtherProject" }
       ]
     }
   }
   ```
   `QueryInstruction` is prepended to the query only (never to indexed chunk text) before it is
   embedded — see [Search quality](#search-quality-the-query-instruction-prefix) below for why,
   and note that changing it does **not** require rebuilding the index. It (and the rest of
   `Embedding`) is shared by every configured project — they all talk to the same Ollama endpoint.

   Each project's `Id` is its cache key — it is deliberately **not** derived from its `Root`, so
   the same project can live at different paths on different machines and still share a cache (see
   [Moving the cache between machines](#moving-the-cache-between-machines)). Every `Id` must be
   unique; the server refuses to start (with a message naming the duplicate) if two projects share
   one.

   Each project also has an optional `Extensions` list controlling which files it indexes —
   defaults to `[".cs", ".razor", ".md"]` when omitted, as above. Set it per project to widen or
   narrow that set, e.g. `{ "Id": "otherproject", "Root": "...", "Extensions": [".cs"] }` to index
   only C# for a project with no Razor UI or docs worth indexing. Matching is case-insensitive.
   See [Which files get indexed](#which-files-get-indexed) below for how chunking differs by
   extension.

   Instead of editing `appsettings.json` directly (and risking committing your local paths), copy
   `src/CodeIndex.Server/appsettings.Local.json.example` to
   `src/CodeIndex.Server/appsettings.Local.json` and put your real `Projects` list there. It is
   loaded after `appsettings.json` and is gitignored, so it never gets committed.
5. **Build the initial index:**
   ```bash
   dotnet run --project src/CodeIndex.Server -c Release -- --build-only
   ```
   This is a one-time, from-scratch build of **every configured project**, one after another.
   Expect several minutes per project for a few hundred files (see the measured numbers below) —
   every chunk has to be embedded once. Subsequent runs of the server refresh incrementally
   instead of repeating this. A project whose `Root` does not exist is reported and skipped rather
   than aborting the whole run.
6. **Check it worked:**
   ```bash
   dotnet run --project src/CodeIndex.Server -c Release -- --status
   ```
   This prints, for every configured project, file/chunk counts, cache location and size, and a
   live refresh + search round trip.
7. **Register the server with Claude Code** — see
   [Registering with Claude Code (manual fallback)](#registering-with-claude-code-manual-fallback)
   below.

## The four tools

Every tool below accepts an optional `project` parameter. Pass it to scope the call to one
configured project; omit it to act on every configured project at once. This matters most for
`code_search`, where omitting `project` searches every project and merges the results into one
ranked list (see [Searching across projects](#searching-across-projects) below for how that merge
works).

### `code_search`

Semantic + symbol search over the indexed source. Prefer this over `Grep` whenever the goal is to
find where something is *implemented*, not to find every literal occurrence of a string.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `query` | string | — | Natural-language question or exact identifier/symbol name. Blank/whitespace-only is rejected with an error rather than returning arbitrary hits. |
| `limit` | int | `5` | Maximum number of hits to return. Negative is rejected with an error; `0` returns none. Not silently capped — a large limit searches deeper into both branches to try to satisfy it. |
| `kind` | string? | `null` | Restrict to one chunk kind: `Class`, `Interface`, `Struct`, `Record`, `Enum`, `Method`, `Constructor`, `Property`, `Field`, or `FileFragment`. Case-insensitive; an unrecognized value is ignored silently. |
| `path_filter` | string? | `null` | Case-insensitive substring filter on the file's relative path. |
| `project` | string? | `null` | Restrict the search to one configured project's `Id`. Omit to search every configured project and merge the results. |

Returns a ranked list of hits, each with an `id`, the `project` it came from, file path, line
range, kind, symbol, signature, doc comment (if any), a short excerpt (at most 5 lines from the
start of the declaration — enough to judge relevance, not a substitute for `code_get_chunk`), an
optional `excerpt_may_be_stale` flag, and a `score`: the fused Reciprocal-Rank-Fusion value (not a raw
similarity percentage — see [Searching across projects](#searching-across-projects) for what feeds
it). Higher is better; a hit near the bottom of only one branch's ranking is a weak match worth a
second look. The vector branch also applies a relevance floor before fusion — see
[Search quality: the query instruction prefix](#search-quality-the-query-instruction-prefix) and
`Embedding:MinCosineSimilarity` below — so a query with no genuine semantic match in the index
does not come back with confident-looking noise. The index refreshes incrementally before every
call. An unrecognized `project` value returns a clear error listing the configured project ids,
rather than an empty result. If a source file changed since the last successful refresh and the
embedding backend is unreachable, `code_search` still returns symbol-branch results from the last
known-good snapshot — including one loaded from disk on the very first call of a freshly started
process, before this server instance has ever refreshed anything itself — with `warning` explaining
that the index is stale (see [Known limitations](#known-limitations)).

`excerpt_may_be_stale: true` appears on a hit-by-hit basis (never as a single blanket flag on the
whole response) when the source file's on-disk size/timestamp no longer match what they were when
this chunk's line range was captured — most commonly because the file was edited during the search
itself (query embedding alone takes ~200 ms warm to ~12 s cold — see
[Measured characteristics](#measured-characteristics) — plenty of time for an edit to land between
the refresh and the excerpt actually being read). The excerpt is still returned either way; treat
the flag as "probably still correct, not verified" rather than discarding the hit.

### `code_get_chunk`

Fetches the full body of one chunk by the `id` returned from a `code_search` hit, for when the
excerpt isn't enough.

| Parameter | Type | Description |
|---|---|---|
| `id` | string | Chunk id from a `code_search` hit's `id` field, e.g. `"myproject:3:4137"`. |

An id is opaque and already names the project it came from — pass it back exactly as `code_search`
returned it; there is nothing to combine it with. An id is `"<project>:<generation>:<ordinal>"`:
the ordinal is a position in **one project's** index as it existed at that specific search, and
`generation` names *which* shape of that index it was captured against. Chunk ids do NOT survive a
reindex (an explicit `code_reindex`, or an automatic refresh that added/removed/reordered chunks
anywhere in that project's file order) — but reusing a stale one is *detected*, not silently
resolved: `generation` is compared against the project's current index generation, and a mismatch
returns a clear "this id is from an older index" error instead of quietly resolving the ordinal
against whatever chunk now happens to occupy that slot. Always take the id from the most recent
`code_search` result. The response may also carry `body_may_be_stale: true` — see
`excerpt_may_be_stale` on `code_search` above; it is the same signal, applied to the one chunk this
call fetched.

### `code_index_status`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `project` | string? | `null` | Report status for one configured project. Omit to report on every configured project. |

Reports how many files/chunks are indexed, which embedding model and dimensionality built it, when
it was last built, and where the on-disk cache lives. Useful to check whether the index is warmed
up, or to diagnose stale/incomplete-looking results. A project whose root does not exist is
reported with an `error` field rather than crashing the whole call.

### `code_reindex`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `project` | string? | `null` | Rebuild one configured project. Omit to rebuild every configured project. |

Forces a full rebuild from scratch — every file is re-chunked and re-embedded, not just what
changed. `code_search` already refreshes incrementally before every call, so this is only needed
when the index seems wrong in a way incremental refresh should already have caught (e.g. after
changing the embedding model, or recovering from a corrupted cache). Slower than a normal search,
and slower still when rebuilding every project.

## Searching across projects

When `project` is omitted, `code_search` asks every configured project for up to `limit` hits and
merges them into one ranked list, re-sorted by score and truncated back down to `limit`.

The merge re-sorts by each hit's already-fused score rather than interleaving projects
round-robin. That is a deliberate choice, not the obvious default: `HybridRanker` already solves
the "these two rankings are not on a comparable scale" problem once *within* a project, by fusing
the vector branch and the symbol branch with Reciprocal Rank Fusion (RRF) instead of blending raw
cosine similarity against a raw symbol-match score. RRF's defining property is that a hit's score
depends only on its rank position within its branch and a fixed constant — never on the raw
similarity/match value — so two different projects' fused scores are already expressed in that
same rank-derived currency: a hit that places first in both branches of project A's search scores
identically to a hit that places first in both branches of project B's search, regardless of how
large or how different the two codebases are. Re-sorting the pooled results by that score is
therefore a principled merge, not an apples-to-oranges comparison of raw similarity values across
unrelated corpora. Round-robin interleaving was rejected because it ignores match strength: a
project with one excellent hit and nine mediocre ones would have its best result alternate with,
and sometimes lose a slot to, another project's mediocre hits purely because of interleaving
order.

**Correction: the paragraph above is only true once the vector branch has a relevance floor.**
"A hit's score depends only on its rank position, never on the raw similarity value" was originally
read as the reason this merge is safe. It is actually the reason an earlier version of it was
broken: rank-only scoring means a project with nothing genuinely relevant still hands its single
best (but weak) vector match "rank 1" — which then fuses to the exact same score as a rank-1 hit
from a project where that match is real. Measured case: alongside a real 8,751-chunk index, an
unrelated seven-chunk project about cooking took **4 of 8** merged slots for the query "where do we
validate trustline deletion", because its best (and only) candidate's cosine similarity of `0.071`
fused to the identical RRF score as the real index's `0.9525` match — RRF never saw either number,
only "rank 1 in a branch of size N." `Embedding:MinCosineSimilarity` (see
[Setup](#manual-setup-build-from-source) and `EmbeddingOptions.MinCosineSimilarity`'s own remarks for how its `0.55` default
was measured) closes that gap: a candidate is excluded before it can receive a rank at all once its
cosine similarity falls below the configured floor, so a project with nothing relevant contributes
zero vector hits — not one disguised as "rank 1" — and the rank-only comparability claim above is
restored to being true of what actually reaches the merge, rather than true only of numbers that
already lied about how good the match was.

Every configured project is refreshed and searched **concurrently**, not one after another. Each
project's index is fully independent — its own on-disk cache, its own file-change tracking, its
own concurrency gate — so nothing about a multi-project search needs to be serialized across
projects except collecting the results. The one thing every project's refresh does share is the
embedding backend (one Ollama endpoint for the whole server), and Ollama itself serializes
inference for a single resident model regardless of how many concurrent requests arrive — so
concurrency buys nothing for the embedding-bound part of a refresh. It buys something real for the
parts that are *not* embedding-bound: the common case of "nothing changed in this project" costs a
stat pass over every file plus Roslyn re-chunking of whatever did change, and neither of those is
serialized by Ollama, so they get to run for every project's files at once instead of one project
at a time.

A project that fails to search (most commonly: its `Root` no longer exists) does not fail the
whole call — its error is folded into the response's `warning` field, prefixed with its project id,
and the merge proceeds with whatever the other projects returned.

Only the first search against a given project actually loads its on-disk cache: constructing the
server touches every configured project just enough to notice a missing `Root` (a cheap directory
check), never enough to read a manifest or vector file, so a server with several large projects
configured still starts fast, and a project you never search never pays any load cost at all.

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

## Cost and accuracy: what four measurements actually showed

This README once claimed semantic search saved "roughly a third to a half" of input tokens on
search-heavy sessions. That was reasoned from the mechanism — fewer wrong files opened, fewer broad
sweeps — and never measured. An external reviewer said so, and he was right. Everything below
replaces it.

**The short version: it does not save tokens.** On both corpora tested, a competent
`Grep`/`Glob`/`Read` agent was cheaper. Reducing the per-call payload roughly halved the gap but did
not close it. Accuracy is slightly worse, not better. The tool's real case is narrower than the one
originally advertised, and it is stated at the end of this section.

### Method

Paired subagents. Both members of a pair get **identical task text**; one is restricted to
`Grep`/`Glob`/`Read`, the other to `code_search`/`code_get_chunk`/`Read`. Token usage is read from
each agent's own completion metadata, never estimated. Answers are verified by reading the cited
code.

From the third measurement onward, correctness is scored against a **verified answer key** — about
120 assertions across the six tasks, each with a `path:line`, marked required or bonus, with known
traps and genuinely ambiguous points flagged so neither side is scored on them. Hits, misses and
**false assertions** are counted separately, because "incomplete" and "confidently wrong" are
different failures.

### Measurement 1 — `XrplCSharp`, 10 short tasks

| | Grep | Semantic | Δ |
|---|---:|---:|---:|
| Total | 859,073 | 924,369 | **+7.6%** |

All 20 answers correct. Semantic lost worst on exact-symbol lookups (+15.2%) — expected, that is
what `Grep` is for — and won once (−12.4%) on a three-file question with no shared greppable term.
Ran against the old defaults (`limit=10`, 15-line excerpts).

### Measurement 2 — `Wallet.git`, 6 long tasks

A .NET/Blazor application rather than an SDK, with tasks requiring 5–15 tool calls each.

**The first attempt was invalidated.** The index had been inflated ~5x by nested git worktrees that
`FileSystemSourceProvider` did not exclude — 4,514 files instead of 907. Duplicated hits cost
`code_search` a full excerpt each time while `Grep` skipped them cheaply. That bug is fixed; the run
was discarded and repeated on a clean index rather than published with a caveat.

| | Grep | Semantic | Δ |
|---|---:|---:|---:|
| Clean index, old defaults | 679,242 | 854,083 | **+25.7%** |

Semantic search lost all six tasks. Per-call cost was the mechanism: **9,086 tokens per call against
`Grep`'s 5,348**, while making 26% *fewer* calls. It found things in fewer searches and lost on what
each search returned.

### The change that followed

Excerpts cut from 15 lines to 5, default `limit` from 10 to 5 — measured at **−54.6%** response size
on eight representative queries. Every hit already carries `signature` and `doc` as separate fields,
and `code_get_chunk` exists for the body.

### Measurements 3 and 4 — the same configuration, twice

| | Grep | Semantic | Δ |
|---|---:|---:|---:|
| Run 3 | 744,219 | 802,098 | +7.8% |
| Run 4 (control, nothing changed) | 684,402 | 789,009 | +15.3% |

**Two identical configurations differed by 7.5 percentage points.** That is the noise floor, and it
is why the +7.8% figure should not be quoted on its own — it caught a cheap run of the baseline.

The noise is almost entirely in the **`Grep` arm**: 744,219 vs 684,402 (8.7% apart) against
semantic's 802,098 vs 789,009 (1.7% apart). The tool under test reproduces; the yardstick moves.

Averaged over three runs of the unchanged `Grep` arm (702,621 tokens):

| | vs. `Grep` mean | tokens per call |
|---|---:|---:|
| Semantic, old defaults | +21.6% | 9,086 |
| Semantic, current defaults | **+13.2%** | 5,888–6,218 |
| `Grep` | — | 4,820–5,348 |

The payload cut removed about half the gap. Note the asymmetry: response size fell 54.6% but total
cost fell ~7%, because a subagent's fixed context dominates its token bill. **Further payload
tuning has little left to give** — this is why no second round of it was attempted.

### Accuracy

Scored against the answer key, both runs of the current build:

| | hits | misses | false | bonus |
|---|---:|---:|---:|---:|
| `Grep`, run 3 | 73 | 9 | 1 | 18 |
| `Grep`, run 4 | 72 | 8 | 3 | 16 |
| Semantic, run 3 | 67 | 13 | 2 | 12 |
| Semantic, run 4 | 67 | 15 | 1 | 9.5 |

**Coverage reproduces**: `Grep` finds ~88% of required facts, semantic ~82%. Semantic scored
*exactly* 67 both times.

**The false-assertion rate does not reproduce.** Run 3 was worse for semantic (2 vs 1), run 4 worse
for `Grep` (3 vs 1). The hypothesis that a cheaper tool buys more confident wrongness is **not
supported** by this data.

The reproducible weakness is different and more specific: **semantic ranking under-retrieves
peripheral files** — platform-guarded variants, alternative implementations, parallel code paths.
Across both runs the semantic arm never opened `Platforms\Windows\App.xaml.cs` when tracing a
lifecycle feature that has a distinct Windows path, and missed a second failure route that existed
alongside the main one. These files are *about* the topic but are not the main place it happens, and
similarity ranking sinks them.

### What holds

- Semantic search costs roughly **10–15% more** than a competent `Grep` agent on these corpora, with
  a noise band of several points. It is not a token-saving tool.
- It reliably makes **fewer tool calls** — the mechanism works; the payload per call is what costs.
- It is **slightly less complete** (82% vs 88% of required facts), reproducibly so.
- It is **not less honest** — no measurable difference in false claims.
- It wins on some task shapes. Tracing one feature through several layers with no shared vocabulary
  favoured it reproducibly (−11% to −14% across two runs).

### What this means in practice

**Reach for `Grep` first whenever you can guess a plausible identifier, file name, or path.** On a
well-named C# codebase that is most of the time, and it is both cheaper and more thorough.

**Reach for `code_search` when you genuinely cannot** — an unfamiliar area, a concept with no
obvious shared term, a feature whose implementation is spread across layers. That is a real case,
and it is the one this tool serves.

**Do not expect it to find the odd corner.** Platform-specific files, dead alternates, and secondary
code paths are exactly what it ranks lowest. If completeness matters — "find *every* caller",
"is this handled on all platforms" — verify with `Grep`.

### Caveats

Two corpora, one language, 6–10 tasks per run, four runs total. Subagent overhead differs from an
interactive session: no follow-up turns, no context carried across a conversation. Two of the six
tasks (`T2`, `T3`) produced results that changed sign between runs and carry no information on their
own; the aggregate is what should be read. Accuracy has two data points, enough to show that
coverage reproduces and that false-assertion rates do not, not enough for a confidence interval.

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

**`QueryInstruction` is a raw prefix, not a template.** The text is prepended to the query
verbatim (`$"{QueryInstruction}{query}"`) — the default value above already spells out the full
`"Instruct: ...\nQuery: "` shape Qwen3-Embedding expects; the code does not add those labels
itself. This matters the moment a different model family is in play: `nomic-embed-text` expects
a plain `"search_query: "` prefix with no `Instruct:`/`Query:` labels at all, and a template that
always inserted those labels could never produce that string for any configured value. See
[Choosing an embedding model](#choosing-an-embedding-model-measured) below for the correct prefix
per model.

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

## Choosing an embedding model (measured)

`qwen3-embedding:4b` holds about **10 GB of VRAM** resident (measured via `ollama ps`, not the
2.5 GB download size — the two are unrelated), which rules it out on a card with less headroom.
Four alternatives were benchmarked against the same corpus (the project's own 773-file,
8,988-chunk `xrplcsharp` reference index) with the same method used to pick `0.55` originally
(see `EmbeddingOptions.MinCosineSimilarity`'s remarks): index a fresh copy of the corpus under its
own cache, run the same seven queries from
[Search quality](#search-quality-the-query-instruction-prefix) above, and — separately — record
top-1 cosine similarity for those and for a set of deliberately irrelevant queries, so the derived
threshold is honest rather than copied from a different model's distribution.

**The symbol branch never changed across any of the five runs — a hardcoded zero.** None of the
seven queries below contain a literal identifier substring `SymbolMatcher` can key on (they are
phrased the way a developer actually asks a question, not the way a class is named), so the
symbol branch returned **zero hits, for every model, on every one of the seven queries** — the
task's premise ("the symbol branch does not depend on the model at all") holds, but it also means
these particular queries have *no* fallback: whatever the vector branch finds is the entire
answer, and a weak model has nothing to fall back on for exactly this kind of natural-language
question. A query that already contains the identifier (`TrustSetFlags`, `ComputeSignature`) would
of course still work via the symbol branch regardless of which embedding model is configured.

| Model | VRAM resident | Download | Dimensions | Index time (773 files) | Cache size | Warm query | 7-query score | Genuine / irrelevant cosine | Derived `MinCosineSimilarity` | `QueryInstruction` |
|---|---|---|---|---|---|---|---|---|---|---|
| **qwen3-embedding:4b** (current default) | 10.0 GB | 2.5 GB | 2560 native → 1024 | 416.5 s (~6.9 min) | 37.7 MB | 216 ms | **7/7** (6 rank 1, 1 rank 3 — see above) | 0.616–0.913 / 0.331–0.539 | **0.55** (existing default confirmed, still the right value on the current, slightly larger corpus) | `"Instruct: Given a developer's question about a codebase, retrieve the C# code that implements it.\nQuery: "` |
| qwen3-embedding:0.6b | **5.8 GB** | 639 MB | 1024 native | 324.4 s (~5.4 min) | 37.7 MB | 294 ms | 3/7 rank 1 (drops→XRP, address parsing, websocket lifecycle) + 2 more at rank 2 (tx hash, trust-line validation) + 1 weak (retry) + 1 miss (payment signing) | 0.573–0.864 / 0.272–0.514 | ~0.53 | same as above (same family, same prefix) |
| nomic-embed-text | 323 MB | 274 MB | 768 native | 120.5 s (~2.0 min) | 28.9 MB | 39 ms | 2/7 rank 1 (drops→XRP, websocket lifecycle); the other five miss or land on a plausible-looking but wrong DTO/property | 0.654–0.787 / 0.450–0.596 | ~0.61 | `"search_query: "` — **not** `nomic`'s own `"search_document: "` on the passage side; see caveat below |
| mxbai-embed-large | 618 MB | 669 MB | 1024 native | 172.1 s (~2.9 min) | 37.7 MB | 45 ms | 2/7 rank 1 (drops→XRP, websocket lifecycle); Markdown guides outrank the actual code on 3 of the remaining 5 | 0.636–0.835 / 0.380–**0.720** | **no clean value exists** — see caveat below | `"Represent this sentence for searching relevant passages: "` |
| all-minilm | **26 MB** | 45 MB | 384 native | 91.9 s (~1.5 min) | **15.8 MB** | 58 ms | 2/7 rank 1 (drops→XRP, address parsing) + 3 more at rank 2–3 (tx hash, trust-line validation, websocket) + 1 weak (retry) + 1 miss (payment signing) | 0.435–0.702 / 0.123–0.262 | ~0.28 | *(none — no asymmetric training)* |

"Genuine / irrelevant cosine" is the same measurement `0.55` was derived from, run per model: top-1
cosine similarity for 14 genuine developer queries (the seven above plus seven more —
"AMM pool trading fee", "escrow finish transaction", etc.) vs. 11 deliberately irrelevant ones
(recipes, hiking, "asdkjfh aslkdjf qwoeiru zxcvnm"). Every hit was verified by opening the file at
the reported path/line range, not accepted on a plausible-looking symbol name — the same standard
the original 7/7 measurement used.

**Two caveats, not swept under the table:**

- **`nomic-embed-text` is measured with only half of its intended asymmetry.** It expects
  `"search_query: "` on the query side and `"search_document: "` on the indexed passage side —
  this project's architecture only ever has a hook for the query side
  (`IEmbeddingClient.EmbedQueryAsync`; see [the query instruction prefix](#search-quality-the-query-instruction-prefix)
  above for why passage text is never touched). The numbers above are a query-side-only
  approximation of how this model is meant to be used, and likely understate its real quality —
  this is a limitation of what this project's config surface can express today, not a limitation
  of the model.
- **`mxbai-embed-large` has no cosine value that reliably tells genuine from irrelevant.** One of
  the eleven irrelevant queries — `"asdkjfh aslkdjf qwoeiru zxcvnm"`, deliberate keyboard-mash
  gibberish — scored **0.720**, higher than the *weakest genuine* query's own 0.636
  ("where do we validate trustline deletion"). Any single threshold either rejects that genuine
  query or admits the gibberish one as if it were a real match. Excluding that one outlier as a
  documented anomaly, the next-highest irrelevant score is 0.506, which would put a workable
  floor around 0.60 — but that is a judgment call about ignoring an inconvenient data point, not a
  clean measurement, and anyone configuring this model should know the floor is unreliable against
  adversarial-looking (or just unlucky) nonsense input.

### Recommendation

- **16 GB+ VRAM to spare, want the best answers:** `qwen3-embedding:4b` (the default) — 7/7 on
  the reference queries, comfortably the best ranking quality measured here.
- **Modest GPU (a few GB free, e.g. a laptop sharing VRAM with a display), still want it usable:**
  `qwen3-embedding:0.6b` is the closest thing to "the default, but lighter" this benchmark found —
  same family, same prefix, no reindex-format surprises — but budget **5.8 GB**, not the 639 MB
  download size: Ollama loads it with a 32K-token context window by default, and that context's
  KV cache, not the model weights, is what actually costs the VRAM. It is a genuinely weaker
  ranker than the 4b default (3/7 clean vs. 7/7), not merely a smaller download.
- **CPU-only or a genuinely tiny GPU:** `all-minilm` is the honest floor of what's usable here —
  26 MB resident, sub-100ms warm queries, a 15.8 MB cache, and it still gets the two clearest
  queries exactly right at rank 1 with three more one or two ranks off. It should be read as "good
  enough to triage, not to trust blindly" — half of the seven reference queries land on a
  plausible-but-wrong chunk at rank 1, which the fused, low-confidence-looking `score` does not by
  itself distinguish from a real hit.
- **`nomic-embed-text` and `mxbai-embed-large` were, on this benchmark, not a clear win over
  `all-minilm` despite costing more VRAM and a larger download.** Both scored 2/7 on the reference
  queries — the same as the 26 MB model — while resident at 323 MB and 618 MB respectively.
  `nomic-embed-text`'s measurement is handicapped by the missing passage-side prefix (see caveat
  above) and may do better with that closed; `mxbai-embed-large`'s unreliable relevance floor is a
  genuine finding, not a measurement artifact. Neither is recommended over `all-minilm` for this
  kind of C#-codebase search task based on what was actually measured, though either may still be
  the right choice in a deployment that already standardized on it for other reasons.
- **A cheap model turning out nearly as good as the expensive one would have been reported as
  such — that did not happen here.** The gap between `qwen3-embedding:4b` (7/7) and every
  alternative (2/7 or 3/7) is large and consistent across the whole model set, not a close call
  that rounds either way.

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

## Pointing it at different repositories

Edit `CodeIndex:Projects` in `src/CodeIndex.Server/appsettings.json` (or, better,
`appsettings.Local.json` — see step 4 above), or override individual entries without touching
either file via environment variables (the `CODEINDEX_` prefix, `__` as the section/array-index
separator):

```bash
export CODEINDEX_CodeIndex__Projects__0__Root="/path/to/OtherProject"
export CODEINDEX_CodeIndex__Projects__0__Id="otherproject"
```

Each distinct `Id` gets its own cache directory under `%LocalAppData%/code-index-mcp/<Id>` (or
wherever that project's `CacheDirectory` points, if set explicitly) — indexing a second project
never touches the first project's cache, and every project's `Id` must be unique (the server
refuses to start otherwise). One server process can index as many projects as `CodeIndex:Projects`
lists; see [The four tools](#the-four-tools) and
[Searching across projects](#searching-across-projects) above for how a request scopes itself to
one project or spans all of them.

## Moving the cache between machines

The on-disk cache (`manifest.json` + `vectors.bin`, ~37 MB for a project this size) can simply be
copied to `%LocalAppData%/code-index-mcp/<Id>` on another machine, even if the project lives under
a different drive letter or path there. Each configured project's cache is independent, so this
works one project at a time — copying `myproject`'s cache never touches `otherproject`'s. It works
at all because of three decisions, each already made for other reasons:

1. **Paths inside the manifest are relative** to the project root — the cache never embeds
   an absolute path or drive letter.
2. **The cache key is a project's `Id`, not a hash of its path** — the same key applies on both
   machines regardless of where the repository actually sits.
3. **Freshness checks fall back to a content hash** when size/timestamp differ — after a
   `git clone` or `git checkout`, every file's modification time changes but content usually
   doesn't, so copying the cache (or just switching branches) doesn't trigger a needless full
   reindex.

There's no automated sync between machines — the source is already synced via git, and the
benefit of automating a 37 MB copy or a several-minute rebuild is smaller than the cost of
building and maintaining that automation.

## Registering with Claude Code (manual fallback)

If you followed [Install as a Claude Code plugin](#install-as-a-claude-code-plugin-recommended)
above, this step is already done — skip it. This section is only for the manual setup path.

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

## Which files get indexed

Each project's `Extensions` list (see [Setup](#manual-setup-build-from-source), step 4) controls which files it walks —
`[".cs", ".razor", ".md"]` by default. Which chunker a file goes through is decided by its
extension, not by trial and error:

- **`.cs`** goes to the structural, Roslyn-based chunker: one chunk per type declaration plus one
  chunk per indexable member (see [The problem this solves](#the-problem-this-solves)). If Roslyn
  produces zero chunks for a particular file (syntax Roslyn can't decompose meaningfully — see
  [Known limitations](#known-limitations)), it falls back to line-window chunking for that file.
- **Everything else — `.razor`, `.md`, and any other configured extension — goes straight to
  line-window chunking**, never through Roslyn. This is a routing decision, not merely "try Roslyn,
  fall back on failure": running the C# parser against Markdown or Razor markup does not throw, it
  silently produces empty or meaningless structural chunks, so routing away from Roslyn entirely up
  front is both faster and more honest than relying on that failure mode to happen to look right.
  Every chunk produced this way carries `kind: "FileFragment"`, same as a `.cs` file that fell back
  to windowing — see [Narrowing to code](#narrowing-to-code-vs-documentation) below for what that
  makes possible.

**`.razor` files are windowed, not structurally parsed, even though they hold C# inside `@code`
blocks.** Roslyn cannot parse a whole `.razor` file as a compilation unit — the surrounding markup
isn't C# — and hand-extracting just the `@code` block's contents would need a real Razor-aware
parser (recognizing directives, distinguishing markup from code, finding the block boundaries
correctly), which does not exist here. Line-window chunking is the honest first step: it indexes
the file's content, imperfectly segmented, rather than skipping the file or pretending a C# parser
can make sense of it. Structured Razor parsing is a separate piece of work, not a natural extension
of the existing chunker.

### Widening `Extensions` on an already-indexed project

Adding an extension to an existing project's `Extensions` (e.g. picking up `.md` for the first
time) does not invalidate anything already indexed — the newly-matched files are indistinguishable
from any other newly-added files as far as [incremental refresh](#the-four-tools) is concerned:
they get chunked and embedded, everything already in the cache is copied through untouched. Verified
against the project's own 724-file, 8,751-chunk `xrplcsharp` reference index: widening `Extensions`
from `.cs`-only to the new default (`.cs`, `.razor`, `.md`) picked up 42 more files (38 `.md`, 4
`.razor`) and 146 more chunks in **21 s** — not the ~7.5 min a from-scratch rebuild of an index this
size costs — and every one of the original 8,751 chunks came back with byte-identical vectors,
confirmed by comparing `vectors.bin` before and after for a random sample. Nothing pre-existing was
re-chunked or re-embedded; only the genuinely new files were.

### Narrowing to code vs. documentation

Widening the default set to include `.md` means a documentation chunk can legitimately outrank the
code it describes — measured on the 766-file, 8,897-chunk `xrplcsharp` reference index: for
implementation-style queries ("where do we validate trustline deletion", "how is a payment
transaction signed", "converting drops to XRP", "retry logic for failed requests", and three more
from the [query-instruction measurement](#search-quality-the-query-instruction-prefix)), no
Markdown chunk entered the top 8 results for any of them — the guides in this particular repository
don't happen to use that vocabulary. But for doc-shaped queries matching this repo's `DocFx/*-Guide.md`
guides — "how does the lending protocol work", "how do I connect to a rippled node", "how does the
cross-chain bridge work", "vault deposit and withdraw overview", "how to set up sponsorship for
account reserves" — a Markdown chunk took **rank 1 in 5 of those 6 queries**, ahead of the code that
actually implements the feature.

This was not fixed by tuning the ranker — there is no principled way to tell "the guide explaining
loans" from "the code implementing loans" apart by score alone; both are legitimately relevant to
"how does the lending protocol work." The fix is `code_search`'s existing `kind` parameter: passing
any concrete kind (`Class`, `Method`, `Property`, etc.) excludes `FileFragment` chunks entirely,
since only line-window chunking ever produces that kind — for every one of the five displaced
queries above, restricting to non-`FileFragment` kinds put the actual implementing code back at or
near rank 1. There is no dedicated "kind != FileFragment" shorthand; querying once per concrete
kind and merging (the same pool-and-re-sort approach [multi-project search](#searching-across-projects)
already uses) is the current way to get "code only" when a query's own vocabulary doesn't already
avoid the overlap.

## Untrusted content

Every source fragment `code_search` and `code_get_chunk` return is wrapped in
`<untrusted-content id="{nonce}" origin="...">...</untrusted-content id="{nonce}">` markers, where
`{nonce}` is a fresh, cryptographically random value generated for that call. The indexed
project's source is still, ultimately, third-party text as far as the agent reading it is
concerned — a comment or string literal inside it could contain text crafted to look like an
instruction ("ignore previous instructions and...") aimed at whatever agent later reads the search
result. The markers tell a downstream agent that follows the standard `untrusted-content`
convention to treat everything between them as data to read, never as instructions to follow,
regardless of what it appears to say.

An earlier version of this wrapper used a fixed marker string and tried to "defuse" any literal
closing-tag substring already inside the indexed content (inserting a zero-width space) so that
indexed source could supposedly never forge its own closing marker. **That guarantee did not
hold**: a single exact, case-sensitive string replace missed a closing tag with an extra space
before `>` (still valid, spec-compliant XML), a different letter case, and — worst of all — the
literal defused form itself, since a zero-width space is invisible and so the "defused" and
genuine markers were visually identical text an attacker could simply write directly. The opening
marker was not defused at all, so forging an apparent exit from untrusted context was also
possible. The fix was not a better defuse routine; it was removing the need for one. Because the
nonce is drawn fresh per call and indexed content has no way to predict it, nothing embedded in
that content can ever equal the current call's `id="{nonce}"`, so it cannot close (or fake-open)
the wrapper regardless of case, spacing, or hidden Unicode tricks.

## Known limitations

- **Cold-start cost.** See [Cold start and `KeepAlive`](#cold-start-and-keepalive) above — even
  with `KeepAlive` set, the *first* query after the model has actually unloaded still pays the
  full ~12 s reload.
- **Not every file gets structural chunks.** Of the 723 `.cs` files in the measured run, 11
  produced no chunks from the Roslyn-based chunker (e.g. syntax that Roslyn can't decompose
  meaningfully) and fell back to plain line-window chunking for that file instead of
  per-declaration chunks. Every non-`.cs` file (`.razor`, `.md`, or anything else configured via
  `Extensions`) is window-chunked unconditionally by design — see
  [Which files get indexed](#which-files-get-indexed) — not as a fallback from a failed attempt.
- **Markdown can outrank the code it documents for doc-shaped queries.** See
  [Narrowing to code vs. documentation](#narrowing-to-code-vs-documentation) for what was measured
  and how to filter it out with `kind` when it matters.
- **Chunk ids do not survive a reindex — but reusing a stale one is now detected, not silently
  wrong.** Each id (e.g. `"myproject:3:4137"`) is an ordinal position in the specific index
  snapshot of *that project* a `code_search` call ran against, plus the generation number of that
  snapshot's chunk-list shape. An explicit `code_reindex`, or even an automatic incremental refresh
  that added/removed/reordered chunks anywhere in that project's file order, bumps the generation —
  and `code_get_chunk` compares it before ever touching the ordinal, so a stale id comes back as a
  clear "this id is from an older index" error rather than resolving to whatever chunk now happens
  to occupy that ordinal. Always fetch a fresh id via `code_search` immediately before calling
  `code_get_chunk`.
- **An excerpt or chunk body can still describe code that has since moved, even with a
  current-generation id.** The generation check above catches an ordinal pointing at the wrong
  *chunk*; it does not catch the file's content moving *within* the same chunk between the refresh
  that computed its line range and the moment the excerpt is actually read — the window is roughly
  the query-embedding latency (see [Measured characteristics](#measured-characteristics)), long
  enough for an edit to land mid-search. `code_search`/`code_get_chunk` detect this per hit by
  comparing the file's current size/timestamp against what they were at that refresh, and set
  `excerpt_may_be_stale`/`body_may_be_stale` when they differ — the excerpt is still returned, just
  flagged as unverified rather than presented as certainly accurate.
- **A project whose `Root` no longer exists is reported, not silently dropped.** `code_search`
  with that `project` explicitly set, or `code_get_chunk` with an id naming it, returns a clear
  error; searching every project skips it with a warning naming it instead of failing the whole
  call. It never prevents the other configured projects from working.
- **A file edit that preserves both size and timestamp is invisible to incremental refresh.**
  Freshness is checked cheaply first by comparing file length and last-write-time against what
  was last indexed, and only falls back to a full content hash when one of those differs. An edit
  that happens to leave both unchanged (contrived, but possible with tooling that rewrites a file
  in place without updating its mtime, or with mtime resolution/clock skew) will not be picked up
  until the next explicit `code_reindex`.
- **A stopped embedding backend degrades a project's search; it does not disable it — even on a
  freshly started server process.** An MCP client starts this server fresh per session, so nothing
  is in memory yet on the very first call. If Ollama is unreachable and a source file changed since
  the on-disk index was last built successfully, the server loads that on-disk snapshot and answers
  from it: symbol-branch matches still come back, `code_get_chunk` still works, and `warning` says
  the index is stale (edits since that last successful build are not reflected). `code_index_status`
  is the one exception: for an explicit single-project status request, a refresh failure is reported
  as a specific `error` (e.g. "Start it with 'ollama serve'") rather than falling back to stale
  numbers — a deliberate choice, on the reasoning that an explicit status check is asking "is this
  working right now," and a raw failure answers that more usefully than silently degraded numbers
  would.

## Development

TDD throughout; see `docs/superpowers/specs/2026-07-27-code-index-mcp-design.md` for the full
design (in Russian) and `docs/superpowers/plans/2026-07-27-code-index-mcp.md` for the
implementation plan. Run the test suite with:

```bash
dotnet test
```

## License

[MIT](LICENSE) © Aleksandr Platonenkov
