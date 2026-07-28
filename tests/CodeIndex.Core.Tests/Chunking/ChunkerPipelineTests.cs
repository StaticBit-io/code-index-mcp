using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

/// <summary>
/// Covers <see cref="ChunkerPipeline"/>'s routing decision: <c>.cs</c> goes to
/// <see cref="RoslynChunker"/> (see <c>FallbackChunkerTests</c> for its own fallback-when-empty
/// coverage), everything else goes straight to <see cref="FallbackChunker"/> without ever being
/// handed to Roslyn at all.
/// </summary>
public sealed class ChunkerPipelineTests
{
    // Deliberately valid, chunkable C# — if this ever reached RoslynChunker, it would produce a
    // Class-kind chunk for "Widget". Routing a non-".cs" path to FallbackChunker instead is what
    // this whole test class is checking, so the source text needs to be a case Roslyn would
    // otherwise chunk successfully, not just syntax garbage it would already ignore.
    private const string ValidCSharpSource = "namespace A;\npublic class Widget { public void Run() { } }";

    [Fact]
    public void ChunkFile_MarkdownFile_IsWindowChunked_NeverSentToRoslyn()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile("docs/README.md", ValidCSharpSource);

        // Had this content been routed to RoslynChunker, it would have produced a Class chunk for
        // "Widget" instead of a single FileFragment window — the only way every chunk here is a
        // FileFragment is if Roslyn was never invoked for a ".md" path.
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.FileFragment, c.Kind));
    }

    [Fact]
    public void ChunkFile_RazorFile_IsWindowChunked_NeverSentToRoslyn()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        string razorSource = "<h1>@Title</h1>\n@code {\n    public string Title { get; set; } = \"Hi\";\n}\n";
        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile("Components/Widget.razor", razorSource);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.FileFragment, c.Kind));
    }

    [Fact]
    public void ChunkFile_CSharpFile_StillGoesToRoslyn()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile("Widget.cs", ValidCSharpSource);

        Assert.Contains(chunks, c => c.Kind == ChunkKind.Class && c.Symbol == "A.Widget");
    }

    [Theory]
    [InlineData("Widget.CS")]
    [InlineData("Widget.Cs")]
    public void ChunkFile_CSharpExtension_IsMatchedCaseInsensitively(string path)
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile(path, ValidCSharpSource);

        Assert.Contains(chunks, c => c.Kind == ChunkKind.Class);
    }
}
