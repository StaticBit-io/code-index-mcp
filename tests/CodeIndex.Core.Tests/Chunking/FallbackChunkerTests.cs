using CodeIndex.Core.Chunking;
using Xunit;

namespace CodeIndex.Core.Tests.Chunking;

public sealed class FallbackChunkerTests
{
    [Fact]
    public void Chunk_SplitsByLineWindowsWithOverlap()
    {
        string source = string.Join('\n', Enumerable.Range(1, 250).Select(i => $"line {i}"));
        FallbackChunker chunker = new(windowLines: 100, overlapLines: 20);

        IReadOnlyList<CodeChunk> chunks = chunker.Chunk("weird.cs", source);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(100, chunks[0].EndLine);
        Assert.Equal(81, chunks[1].StartLine);
        Assert.All(chunks, c => Assert.Equal(ChunkKind.FileFragment, c.Kind));
    }

    [Fact]
    public void Chunk_UsesFilePathAndLineRangeAsSymbol()
    {
        FallbackChunker chunker = new(windowLines: 100, overlapLines: 20);

        CodeChunk chunk = chunker.Chunk("a/b.cs", "one\ntwo").Single();

        Assert.Equal("a/b.cs:1-2", chunk.Symbol);
    }

    [Fact]
    public void ChunkFile_FallsBackWhenRoslynFindsNoTypes()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile("Program.cs", "Console.WriteLine(\"hi\");");

        Assert.Single(chunks);
        Assert.Equal(ChunkKind.FileFragment, chunks[0].Kind);
    }

    [Fact]
    public void ChunkFile_PrefersRoslynWhenTypesExist()
    {
        ChunkerPipeline pipeline = new(new RoslynChunker(), new FallbackChunker());

        IReadOnlyList<CodeChunk> chunks = pipeline.ChunkFile(
            "W.cs",
            "namespace A;\npublic class W { public void M() { } }");

        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.FileFragment);
    }

    [Fact]
    public void Constructor_Throws_WhenWindowLinesIsLessThanOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackChunker(windowLines: 0, overlapLines: -5));
    }

    [Fact]
    public void Constructor_Throws_WhenOverlapLinesIsNegative()
    {
        // Regression for the case measured on a 12-line file: negative overlap advanced the
        // window by more than its own length (stride = windowLines - overlapLines > windowLines),
        // so lines fell in the gap between windows and never appeared in any chunk.
        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackChunker(windowLines: 3, overlapLines: -2));
    }

    [Fact]
    public void Constructor_Throws_WhenOverlapLinesIsNotSmallerThanWindowLines()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackChunker(windowLines: 5, overlapLines: 5));
    }

    [Fact]
    public void Constructor_Throws_WhenBothArgumentsAreNegative()
    {
        // Regression for the case measured on a 12-line file: both negative produced chunks
        // with negative line numbers in the symbol instead of failing fast.
        Assert.Throws<ArgumentOutOfRangeException>(() => new FallbackChunker(windowLines: -5, overlapLines: -10));
    }

    [Fact]
    public void Chunk_CapsEmbedTextBodyAtTwoThousandCharacters()
    {
        FallbackChunker chunker = new(windowLines: 1, overlapLines: 0);
        string body = new string('a', 3000);

        CodeChunk chunk = chunker.Chunk("Big.cs", body).Single();
        string codeSection = ExtractCodeSection(chunk.EmbedText);

        Assert.Equal(2000, codeSection.Length);
    }

    [Fact]
    public void Chunk_TruncatesBodyWithoutSplittingSurrogatePair()
    {
        // Same boundary case as RoslynChunker's truncation test: a surrogate pair (an emoji)
        // straddling the 2000-character cut must not be split, since both chunkers share the
        // same truncation helper and the same downstream embedding model.
        const string emoji = "😀";
        string filler = new string('a', 1999);
        string body = filler + emoji;
        FallbackChunker chunker = new(windowLines: 1, overlapLines: 0);

        CodeChunk chunk = chunker.Chunk("Big.cs", body).Single();
        string codeSection = ExtractCodeSection(chunk.EmbedText);

        Assert.False(char.IsHighSurrogate(codeSection[^1]));
    }

    private static string ExtractCodeSection(string embedText)
    {
        int codeIndex = embedText.IndexOf("Code:\n", StringComparison.Ordinal) + "Code:\n".Length;
        return embedText[codeIndex..];
    }

    // NOTE: no test exercises the "RoslynChunker throws" branch of ChunkerPipeline directly.
    // Direct probing of RoslynChunker (syntax garbage, lone surrogates, embedded NULs,
    // unterminated literals, a 20 MB file) found no case where it throws a catchable
    // exception — it returns zero chunks instead, which the pipeline already treats as "fall
    // back". The one confirmed failure mode, a StackOverflowException from roughly 4000
    // levels of nesting, terminates the process and cannot be caught by any try/catch, so it
    // cannot be exercised by a test either. `Chunk(path, null!)` does throw
    // ArgumentNullException, but that is not a meaningful regression test: after the pipeline
    // catches it, FallbackChunker.Chunk is invoked with the same null source and fails with
    // NullReferenceException instead, so the test would not demonstrate the fallback actually
    // working. Substituting a throwing chunker instead would require either adding an
    // interface (not requested for this task) or removing `sealed`/adding `virtual` to
    // RoslynChunker (a production-code change outside this task's scope).
}
