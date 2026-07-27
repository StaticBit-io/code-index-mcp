using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class RoslynChunkerEdgeCaseTests
{
    private readonly RoslynChunker _chunker = new();

    [Fact]
    public void Chunk_HandlesFileScopedNamespace()
    {
        const string source = """
            namespace Acme.Core;

            public class Widget
            {
                public void Spin() { }
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Widget.cs", source);

        Assert.Contains(chunks, c => c.Symbol == "Acme.Core.Widget.Spin");
    }

    [Fact]
    public void Chunk_HandlesNestedTypes()
    {
        const string source = """
            namespace Acme;

            public class Outer
            {
                public class Inner
                {
                    public void Go() { }
                }
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Outer.cs", source);

        Assert.Contains(chunks, c => c.Symbol == "Acme.Outer.Inner");
        Assert.Contains(chunks, c => c.Symbol == "Acme.Outer.Inner.Go");
    }

    [Fact]
    public void Chunk_HandlesRecordsAndPositionalParameters()
    {
        const string source = """
            namespace Acme;

            public record Money(decimal Amount, string Currency)
            {
                public Money Doubled() => this with { Amount = Amount * 2 };
            }
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Money.cs", source);

        Assert.Contains(chunks, c => c.Kind == ChunkKind.Record && c.Symbol == "Acme.Money");
        Assert.Contains(chunks, c => c.Symbol == "Acme.Money.Doubled");
    }

    [Fact]
    public void Chunk_HandlesGenericMethodSignature()
    {
        const string source = """
            namespace Acme;

            public class Box
            {
                public T? Unwrap<T>(object value) where T : class => value as T;
            }
            """;

        CodeChunk method = _chunker.Chunk("Box.cs", source).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal("public T? Unwrap<T>(object value)", method.Signature);
    }

    [Fact]
    public void Chunk_TreatsPartialHalvesIndependently()
    {
        const string first = """
            namespace Acme;

            public partial class Service
            {
                public void One() { }
            }
            """;
        const string second = """
            namespace Acme;

            public partial class Service
            {
                public void Two() { }
            }
            """;

        IReadOnlyList<CodeChunk> a = _chunker.Chunk("Service.A.cs", first);
        IReadOnlyList<CodeChunk> b = _chunker.Chunk("Service.B.cs", second);

        Assert.Contains(a, c => c.Symbol == "Acme.Service.One" && c.FilePath == "Service.A.cs");
        Assert.Contains(b, c => c.Symbol == "Acme.Service.Two" && c.FilePath == "Service.B.cs");
    }

    [Fact]
    public void Chunk_ReturnsEmptyForTopLevelStatementsWithoutTypes()
    {
        const string source = """
            Console.WriteLine("hello");
            """;

        IReadOnlyList<CodeChunk> chunks = _chunker.Chunk("Program.cs", source);

        Assert.Empty(chunks);
    }
}
