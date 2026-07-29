using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class RoslynChunkerTests
{
    private const string Sample = """
        namespace Acme.Payments
        {
            /// <summary>Charges cards.</summary>
            public class PaymentGateway
            {
                public int Charge(decimal amount)
                {
                    return 1;
                }
            }
        }
        """;

    [Fact]
    public void Chunk_ProducesTypeAndMemberChunks()
    {
        RoslynChunker chunker = new();

        IReadOnlyList<CodeChunk> chunks = chunker.Chunk("src/PaymentGateway.cs", Sample);

        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Class && c.Symbol == "Acme.Payments.PaymentGateway");
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Method && c.Symbol == "Acme.Payments.PaymentGateway.Charge");
    }

    [Fact]
    public void Chunk_CapturesSignatureAndDocComment()
    {
        RoslynChunker chunker = new();

        CodeChunk type = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Class);
        CodeChunk method = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Method);

        Assert.Contains("Charges cards.", type.DocComment, StringComparison.Ordinal);
        Assert.Equal("public int Charge(decimal amount)", method.Signature);
    }

    [Fact]
    public void Chunk_RecordsOneBasedInclusiveLineRange()
    {
        RoslynChunker chunker = new();

        CodeChunk method = chunker.Chunk("src/PaymentGateway.cs", Sample).Single(c => c.Kind == ChunkKind.Method);

        Assert.Equal(6, method.StartLine);
        Assert.Equal(9, method.EndLine);
    }
}
