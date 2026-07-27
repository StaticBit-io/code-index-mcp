using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

/// <summary>
/// Coverage added after running the chunker over a real 719-file C# codebase surfaced
/// gaps: C# 14 extension blocks, missing signature modifiers, unindexed constants,
/// operators/indexers/events/delegates, raw XML in doc comments, and a surrogate-pair
/// truncation hazard.
/// </summary>
public sealed class RoslynChunkerCoverageTests
{
    private readonly RoslynChunker _chunker = new();

    [Fact]
    public void Chunk_SkipsExtensionBlockButIndexesItsMembersWithCleanSymbol()
    {
        const string source = """
            namespace N
            {
                public static class Ext
                {
                    extension(string s)
                    {
                        public bool IsLong => s.Length > 10;
                    }
                }
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Ext.cs", source);

        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.Unknown);
        Assert.Contains(chunks, c => c.Symbol == "N.Ext.IsLong");
        Assert.DoesNotContain(chunks, c => c.Symbol.Contains("..", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunk_ExtensionMethodPreservesThisModifier()
    {
        const string source = """
            namespace Acme;

            public static class StringExtensions
            {
                public static bool IsLpToken(this string currency) => currency.Length > 2;
            }
            """;

        CodeChunk method = _chunker.Chunk("StringExtensions.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("public static bool IsLpToken(this string currency)", method.Signature);
    }

    [Fact]
    public void Chunk_MethodSignaturePreservesParameterModifiers()
    {
        const string source = """
            namespace Acme;

            public class Parser
            {
                public static bool TryCombine(ref int a, out int b, params int[] values)
                {
                    b = a;
                    return true;
                }
            }
            """;

        CodeChunk method = _chunker.Chunk("Parser.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("public static bool TryCombine(ref int a, out int b, params int[] values)", method.Signature);
    }

    [Fact]
    public void Chunk_MethodSignaturePreservesAsyncModifier()
    {
        const string source = """
            namespace Acme;

            public class Fetcher
            {
                public async Task<int> FetchAsync() => 1;
            }
            """;

        CodeChunk method = _chunker.Chunk("Fetcher.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("public async Task<int> FetchAsync()", method.Signature);
    }

    [Fact]
    public void Chunk_IndexesConstField()
    {
        const string source = """
            namespace Acme;

            public class Protocol
            {
                public const string TransactionType = "Payment";
            }
            """;

        CodeChunk field = _chunker.Chunk("Protocol.cs", source).Single(c => c.Kind == ChunkKind.Field);

        Assert.Equal("Acme.Protocol.TransactionType", field.Symbol);
        Assert.Equal("public const string TransactionType", field.Signature);
    }

    [Fact]
    public void Chunk_IndexesStaticReadonlyField()
    {
        const string source = """
            namespace Acme;

            public class Registry
            {
                public static readonly string[] KnownTypes = new[] { "A", "B" };
            }
            """;

        CodeChunk field = _chunker.Chunk("Registry.cs", source).Single(c => c.Kind == ChunkKind.Field);

        Assert.Equal("Acme.Registry.KnownTypes", field.Symbol);
        Assert.Equal("public static readonly string[] KnownTypes", field.Signature);
    }

    [Fact]
    public void Chunk_DoesNotIndexPlainInstanceField()
    {
        const string source = """
            namespace Acme;

            public class Widget
            {
                private int _count;
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Widget.cs", source);

        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.Field);
        Assert.Single(chunks);
    }

    [Fact]
    public void Chunk_IndexesArithmeticOperator()
    {
        const string source = """
            namespace Acme;

            public readonly struct Amount
            {
                public static Amount operator *(Amount left, Amount right) => left;
            }
            """;

        CodeChunk method = _chunker.Chunk("Amount.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("Acme.Amount.operator *", method.Symbol);
    }

    [Fact]
    public void Chunk_IndexesConversionOperator()
    {
        const string source = """
            namespace Acme;

            public readonly struct Amount
            {
                public static implicit operator string(Amount value) => value.ToString();
            }
            """;

        CodeChunk method = _chunker.Chunk("Amount.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("Acme.Amount.implicit operator string", method.Symbol);
    }

    [Fact]
    public void Chunk_IndexesIndexer()
    {
        const string source = """
            namespace Acme;

            public class Enumeration
            {
                public string this[string key] => key;
            }
            """;

        CodeChunk property = _chunker.Chunk("Enumeration.cs", source).Single(c => c.Kind == ChunkKind.Property);

        Assert.Equal("Acme.Enumeration.this[]", property.Symbol);
    }

    [Fact]
    public void Chunk_IndexesEventField()
    {
        const string source = """
            namespace Acme;

            public class Publisher
            {
                public event EventHandler? Updated;
            }
            """;

        CodeChunk field = _chunker.Chunk("Publisher.cs", source).Single(c => c.Kind == ChunkKind.Field);

        Assert.Equal("Acme.Publisher.Updated", field.Symbol);
        Assert.Equal("public event EventHandler? Updated", field.Signature);
    }

    [Fact]
    public void Chunk_IndexesTopLevelDelegate()
    {
        const string source = """
            namespace Acme;

            public delegate void Handler(int value);
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Handler.cs", source);

        Assert.Contains(chunks, c => c.Kind == ChunkKind.Method && c.Symbol == "Acme.Handler");
    }

    [Fact]
    public void Chunk_IndexesNestedDelegateExactlyOnce()
    {
        const string source = """
            namespace Acme;

            public class Container
            {
                public delegate void Callback();
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Container.cs", source);

        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Method && c.Symbol == "Acme.Container.Callback");
    }

    [Fact]
    public void Chunk_TypeLevelChunkListsOperatorsIndexersAndNestedDelegatesAsMembers()
    {
        // The type-level chunk's body is the comma-joined member name list built by
        // GetMemberDisplayNames; every kind indexed as its own member chunk elsewhere must also
        // show up here, or semantic search against "the type as a whole" loses that signal.
        const string source = """
            namespace Acme;

            public readonly struct Amount
            {
                public static Amount operator *(Amount left, Amount right) => left;
                public static implicit operator string(Amount value) => value.ToString();
                public string this[string key] => key;
                public delegate void Callback();
            }
            """;

        CodeChunk type = _chunker.Chunk("Amount.cs", source).Single(c => c.Kind == ChunkKind.Struct);

        Assert.Contains("operator *", type.EmbedText, StringComparison.Ordinal);
        Assert.Contains("implicit operator string", type.EmbedText, StringComparison.Ordinal);
        Assert.Contains("this[]", type.EmbedText, StringComparison.Ordinal);
        Assert.Contains("Callback", type.EmbedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_StripsXmlTagsFromDocCommentKeepingText()
    {
        const string source = """
            namespace Acme;

            public class Calculator
            {
                /// <summary>Adds two numbers.</summary>
                /// <param name="a">The first value.</param>
                /// <returns>The sum.</returns>
                public int Add(int a, int b) => a + b;
            }
            """;

        CodeChunk method = _chunker.Chunk("Calculator.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.False(method.DocComment.Contains('<', StringComparison.Ordinal));
        Assert.False(method.DocComment.Contains('>', StringComparison.Ordinal));
        Assert.Contains("Adds two numbers.", method.DocComment, StringComparison.Ordinal);
        Assert.Contains("The first value.", method.DocComment, StringComparison.Ordinal);
        Assert.Contains("The sum.", method.DocComment, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_StripsMultiLineDocCommentMarkers()
    {
        const string source = """
            namespace Acme;

            public class Calculator
            {
                /**
                 * <summary>Multiplies two numbers.</summary>
                 */
                public int Multiply(int a, int b) => a * b;
            }
            """;

        CodeChunk method = _chunker.Chunk("Calculator.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Contains("Multiplies two numbers.", method.DocComment, StringComparison.Ordinal);
        Assert.False(method.DocComment.Contains("/**", StringComparison.Ordinal));
        Assert.False(method.DocComment.Contains("*/", StringComparison.Ordinal));
    }

    [Fact]
    public void Chunk_RecordClassSignatureKeepsClassKeyword()
    {
        const string source = """
            namespace Acme;

            public record class Money(decimal Amount);
            """;

        CodeChunk type = _chunker.Chunk("Money.cs", source).Single(c => c.Kind == ChunkKind.Record);

        Assert.Equal("public record class Money", type.Signature);
    }

    [Fact]
    public void Chunk_TruncatesBodyWithoutSplittingSurrogatePair()
    {
        // Craft a method whose source text is long enough to be truncated at exactly the
        // ChunkTextLimits.MaxBodyLength (2000-character) boundary, with a surrogate pair (an
        // emoji) straddling that boundary: the high surrogate lands as the last character a
        // naive Substring(0, 2000) would keep, orphaning its low-surrogate partner just past
        // the cut. ChunkTextLimits is internal to CodeIndex.Core (no InternalsVisibleTo to this
        // test assembly), so the boundary is mirrored here as a literal rather than referenced
        // directly — kept in sync by the two assertions below, which fail loudly if this test's
        // 2000/1999 drifts from the real limit instead of quietly asserting nothing.
        const int maxBodyLength = 2000;
        const string emoji = "😀";
        const string prefix = "public void Big() { /*";
        const string suffix = "*/ }";

        string filler = new string('a', (maxBodyLength - 1) - prefix.Length);
        string methodBody = prefix + filler + emoji + suffix;
        string source = $$"""
            namespace Acme;

            public class BigBody
            {
                {{methodBody}}
            }
            """;

        CodeChunk method = _chunker.Chunk("BigBody.cs", source).Single(c => c.Kind == ChunkKind.Method);

        int codeIndex = method.EmbedText.IndexOf("Code:\n", StringComparison.Ordinal) + "Code:\n".Length;
        string codeSection = method.EmbedText[codeIndex..];

        // The fixture's raw method body is well over the limit, so if truncation silently
        // stopped happening (e.g. ChunkTextLimits.MaxBodyLength changed or the truncation call
        // was dropped) this would catch it — a same-length comparison would leave the surrogate
        // assertion below vacuously true regardless of whether truncation actually ran.
        Assert.True(codeSection.Length < methodBody.Length,
            "expected the body to be truncated; fixture and limit may have drifted apart");
        Assert.True(codeSection.Length <= maxBodyLength);
        Assert.False(char.IsHighSurrogate(codeSection[^1]));
    }
}
