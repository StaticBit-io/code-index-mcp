using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using CodeIndex.Core.Tests.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeIndex.Core.Tests.Integration;

/// <summary>
/// Golden-query suite over a small synthetic project shaped like the target codebase (an XRPL
/// client library): namespaces <c>Xrpl.Client</c>, <c>Xrpl.Models</c>, <c>Xrpl.Ledger</c>; types
/// such as <c>XrplClient</c> (with an <c>AccountInfo</c> method), <c>TrustSetFlags</c> (with a
/// <c>SetNoRipple</c> constant), and <c>OfferBook</c> (with a <c>Cancel</c> method).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this suite can and cannot tell you.</b> Every test here runs against
/// <see cref="StubEmbeddingClient"/>, whose vector for a given input is a deterministic
/// function of that input's SHA-256 hash (see its own remarks) — it carries <em>zero</em>
/// semantic signal. A query and a chunk that mean completely different things can land
/// arbitrarily close or far apart in that vector space; only literal identifier overlap (handled
/// by <see cref="SymbolMatcher"/>, not the vector branch) is meaningful here.
/// </para>
/// <para>
/// So this suite verifies the <em>machinery</em>: that the symbol branch finds an exact/prefix/
/// substring identifier match, that <see cref="HybridRanker"/> fusion surfaces it near the top
/// even alongside a vector branch that is pure noise, that <see cref="ChunkKind"/>/path
/// filtering does not accidentally exclude it, and that an edit is picked up by the next search
/// without an explicit rebuild. It does <em>not</em>, and structurally <em>cannot</em>, measure
/// embedding quality — a green run here says nothing about whether a natural-language query like
/// "where are trust lines validated" would find the right code with a real embedding model.
/// That question can only be answered manually, against a real Ollama model, which is exactly
/// what the project's final manual-verification task is for. Do not read a passing run of this
/// file as evidence of semantic search quality.
/// </para>
/// </remarks>
public sealed class SearchQualityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-quality-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private CodeIndexService CreateService(ISourceProvider source, IEmbeddingClient? embedder = null)
    {
        embedder ??= new StubEmbeddingClient();

        IndexStore store = new(_dir);
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());
        CodeIndexOptions options = new() { ProjectId = "search-quality-test" };
        IndexBuilder builder = new(source, pipeline, embedder, store, Options.Create(options));
        return new CodeIndexService(builder, source, embedder);
    }

    private static InMemorySourceProvider CreateSyntheticProject()
    {
        return new InMemorySourceProvider(new Dictionary<string, string>
        {
            ["src/Xrpl/Client/XrplClient.cs"] = BuildXrplClientSource(),
            ["src/Xrpl/Models/TrustSetFlags.cs"] = TrustSetFlagsSource,
            ["src/Xrpl/Ledger/OfferBook.cs"] = OfferBookSource,
            ["src/Xrpl/Ledger/Escrow.cs"] = EscrowSource,
        });
    }

    /// <summary>
    /// One golden query/expected-symbol pair per row. Deliberately spans every
    /// <see cref="SymbolMatcher"/> match tier (exact leaf, prefix, substring/dotted
    /// disambiguation) and every relevant <see cref="ChunkKind"/> (class, method, const field),
    /// so a regression in any one tier or chunk kind shows up as a specific failing row rather
    /// than a single opaque assertion.
    /// </summary>
    [Theory]
    [InlineData("AccountInfo", "Xrpl.Client.XrplClient.AccountInfo")]
    [InlineData("XrplClient", "Xrpl.Client.XrplClient")]
    [InlineData("ClientOptions", "Xrpl.Client.ClientOptions")]
    [InlineData("SetNoRipple", "Xrpl.Models.TrustSetFlags.SetNoRipple")]
    [InlineData("ClearNoRipple", "Xrpl.Models.TrustSetFlags.ClearNoRipple")]
    [InlineData("TrustSet", "Xrpl.Models.TrustSetFlags")]
    [InlineData("LedgerEntry", "Xrpl.Ledger.LedgerEntry")]
    // "Cancel" alone is ambiguous on purpose (OfferBook.Cancel AND Escrow.Cancel both exist,
    // mirroring OfferCancel/EscrowCancel in the real target codebase); the dotted, qualified
    // form disambiguates via SymbolMatcher's substring tier against the full symbol.
    [InlineData("OfferBook.Cancel", "Xrpl.Ledger.OfferBook.Cancel")]
    [InlineData("Escrow.Cancel", "Xrpl.Ledger.Escrow.Cancel")]
    [InlineData("Finish", "Xrpl.Ledger.Escrow.Finish")]
    public async Task SearchWithStatusAsync_GoldenQuery_FindsExpectedSymbolInTopThree(string query, string expectedSymbol)
    {
        InMemorySourceProvider source = CreateSyntheticProject();
        CodeIndexService service = CreateService(source);

        SearchResult result = await service.SearchWithStatusAsync(
            query, limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.True(result.Hits.Count <= 3, "Query asked for top 3 but got more hits back.");
        Assert.Contains(result.Hits, h => h.Chunk.Symbol == expectedSymbol);
    }

    [Fact]
    public async Task FullEditCycle_IndexModifySearchFindsNewSymbolWithoutAnExplicitRebuild()
    {
        InMemorySourceProvider source = CreateSyntheticProject();
        CodeIndexService service = CreateService(source);

        SearchResult before = await service.SearchWithStatusAsync(
            "ClaimPaymentChannel", limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(before.Hits, h => h.Chunk.Symbol == "Xrpl.Client.XrplClient.ClaimPaymentChannel");

        // Simulate an on-disk edit adding a brand-new method to an existing file. No RebuildAsync
        // call anywhere in this test — the next SearchWithStatusAsync must refresh on its own.
        const string NewMethod = """

                    public string ClaimPaymentChannel(string channelId)
                    {
                        return channelId;
                    }

            """;
        source.Set("src/Xrpl/Client/XrplClient.cs", BuildXrplClientSource(NewMethod));

        SearchResult after = await service.SearchWithStatusAsync(
            "ClaimPaymentChannel", limit: 3, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(after.Hits, h => h.Chunk.Symbol == "Xrpl.Client.XrplClient.ClaimPaymentChannel");
    }

    [Fact]
    public async Task SearchWithStatusAsync_GoldenQuery_RespectsKindFilter()
    {
        InMemorySourceProvider source = CreateSyntheticProject();
        CodeIndexService service = CreateService(source);

        // "TrustSet" prefix-matches the TrustSetFlags class itself (ChunkKind.Class); asking
        // only for methods must exclude it even though it would otherwise win outright.
        SearchResult result = await service.SearchWithStatusAsync(
            "TrustSet", limit: 5, kind: ChunkKind.Method, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Hits, h => h.Chunk.Symbol == "Xrpl.Models.TrustSetFlags");
        Assert.All(result.Hits, h => Assert.Equal(ChunkKind.Method, h.Chunk.Kind));
    }

    [Fact]
    public async Task SearchWithStatusAsync_GoldenQuery_RespectsPathFilter()
    {
        InMemorySourceProvider source = CreateSyntheticProject();
        CodeIndexService service = CreateService(source);

        // Both OfferBook.Cancel and Escrow.Cancel exist; a path filter naming only Escrow.cs
        // must surface Escrow.Cancel even though a plain "Cancel" query alone is ambiguous.
        SearchResult result = await service.SearchWithStatusAsync(
            "Cancel", limit: 3, kind: null, pathFilter: "Escrow.cs", TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Equal("src/Xrpl/Ledger/Escrow.cs", h.Chunk.FilePath));
        Assert.Contains(result.Hits, h => h.Chunk.Symbol == "Xrpl.Ledger.Escrow.Cancel");
    }

    private static string BuildXrplClientSource(string extraMethodBody = "") => $$"""
        namespace Xrpl.Client
        {
            /// <summary>Talks to a rippled node over its WebSocket API.</summary>
            public class XrplClient
            {
                public AccountInfoResult AccountInfo(string account)
                {
                    return new AccountInfoResult();
                }

                public AccountLinesResult AccountLines(string account)
                {
                    return new AccountLinesResult();
                }

                public void Connect(string url)
                {
                }

                public void Disconnect()
                {
                }

                public string Submit(string signedTransactionBlob)
                {
                    return signedTransactionBlob;
                }
        {{extraMethodBody}}
            }

            public class ClientOptions
            {
                public string Endpoint { get; set; } = string.Empty;

                public int TimeoutMs { get; set; }
            }

            public class AccountInfoResult
            {
                public string Account { get; set; } = string.Empty;
            }

            public class AccountLinesResult
            {
                public string Account { get; set; } = string.Empty;
            }
        }
        """;

    private const string TrustSetFlagsSource = """
        namespace Xrpl.Models
        {
            /// <summary>Flags accepted by a TrustSet transaction.</summary>
            public static class TrustSetFlags
            {
                public const uint SetNoRipple = 0x00020000;
                public const uint ClearNoRipple = 0x00040000;
                public const uint SetFreeze = 0x00100000;
                public const uint ClearFreeze = 0x00200000;
            }

            public class TrustLine
            {
                public string Currency { get; set; } = string.Empty;

                public string Balance { get; set; } = string.Empty;

                public string Limit { get; set; } = string.Empty;
            }
        }
        """;

    private const string OfferBookSource = """
        namespace Xrpl.Ledger
        {
            /// <summary>One side of the order book between two currencies.</summary>
            public class OfferBook
            {
                public void Cancel(uint sequence)
                {
                }

                public void Create(string takerGets, string takerPays)
                {
                }

                public bool Match(OfferBook other)
                {
                    return false;
                }
            }

            public class LedgerEntry
            {
                public string Index { get; set; } = string.Empty;

                public uint Sequence { get; set; }
            }
        }
        """;

    private const string EscrowSource = """
        namespace Xrpl.Ledger
        {
            /// <summary>A time- or condition-locked XRP escrow.</summary>
            public class Escrow
            {
                public void Create(string destination, uint amountDrops)
                {
                }

                public void Finish(uint sequence)
                {
                }

                public void Cancel(uint sequence)
                {
                }
            }
        }
        """;
}
