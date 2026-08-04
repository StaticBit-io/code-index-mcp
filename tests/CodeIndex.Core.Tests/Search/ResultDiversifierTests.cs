using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class ResultDiversifierTests
{
    /// <summary>
    /// The exact shape of the reproduced defect, reduced to a pure ranking fixture: file "A" (the
    /// dominant/central file — standing in for <c>AuthStateProvider.cs</c>/<c>App.xaml.cs</c> in the
    /// auto-lock trace, or <c>NetworkUnavailableModal.razor.cs</c> in the network-failure trace)
    /// supplies the four highest-ranked candidates, and three other, unrelated-to-each-other files
    /// ("C", "D", "E" — standing in for the Windows-only auto-lock wiring in
    /// <c>Platforms/Windows/App.xaml.cs</c>, the iOS background-task handling, and similar genuinely
    /// distinct peripheral implementations) each supply exactly one relevant candidate, ranked just
    /// below "A"'s four. A plain <c>Take(5)</c> over the fused order — the pre-fix behaviour —
    /// returns four hits from "A" and only one of the three peripheral files, leaving the other two
    /// entirely unrepresented even though each has a hit that outranks "A"'s own 3rd/4th members. With
    /// the default cap (2 per file), "A" is limited to its top two, freeing three slots that go to
    /// all three peripheral files instead of just one.
    /// </summary>
    [Fact]
    public void Diversify_OneFileDominatesTheRanking_SpreadsTheFreedSlotsAcrossPeripheralFilesInsteadOfBackfillingTheSameFile()
    {
        ScoredIndex[] ranked =
        [
            new(0, 0.95f), // A
            new(1, 0.90f), // A
            new(2, 0.85f), // A — would be 3rd A slot in a plain Take(5)
            new(3, 0.80f), // A — would be 4th A slot in a plain Take(5)
            new(4, 0.75f), // C — the only peripheral file a plain Take(5) would ever reach
            new(5, 0.70f), // D — entirely invisible to a plain Take(5)
            new(6, 0.65f), // E — entirely invisible to a plain Take(5)
        ];

        string FilePathOf(int index) => index switch
        {
            <= 3 => "src/A.cs",
            4 => "src/C.cs",
            5 => "src/D.cs",
            _ => "src/E.cs",
        };

        IReadOnlyList<ScoredIndex> preFix = ranked.OrderByDescending(r => r.Score).Take(5).ToArray();
        Assert.Equal(4, preFix.Count(r => FilePathOf(r.Index) == "src/A.cs"));
        Assert.DoesNotContain(preFix, r => FilePathOf(r.Index) == "src/D.cs");
        Assert.DoesNotContain(preFix, r => FilePathOf(r.Index) == "src/E.cs");

        IReadOnlyList<ScoredIndex> diversified = ResultDiversifier.Diversify(ranked, FilePathOf, limit: 5);

        Assert.Equal(5, diversified.Count);
        Assert.Equal(2, diversified.Count(r => FilePathOf(r.Index) == "src/A.cs"));
        // The two strongest members of the dominant file are still both present...
        Assert.Contains(diversified, r => r.Index == 0);
        Assert.Contains(diversified, r => r.Index == 1);
        // ...and now every peripheral file gets its one relevant hit — not just "C", the one a
        // plain Take(5) happened to still reach.
        Assert.Contains(diversified, r => r.Index == 4); // C
        Assert.Contains(diversified, r => r.Index == 5); // D
        Assert.Contains(diversified, r => r.Index == 6); // E
    }

    [Fact]
    public void Diversify_FewerCandidatesThanLimit_ReturnsAllOfThemUnchanged()
    {
        ScoredIndex[] ranked = [new(0, 0.9f), new(1, 0.8f)];

        IReadOnlyList<ScoredIndex> diversified = ResultDiversifier.Diversify(
            ranked, _ => "src/Only.cs", limit: 5);

        Assert.Equal(ranked, diversified);
    }

    [Fact]
    public void Diversify_MoreCandidatesThanLimitButAllOneFile_StillReturnsLimitEntriesNotFewer()
    {
        // The "genuinely five members of one class" case: capping must never cause the method to
        // return fewer than `limit` results when at least `limit` candidates exist overall — the
        // backfill pass has to reach into the same file once every other file is exhausted (here,
        // there is no other file at all).
        ScoredIndex[] ranked = [new(0, 0.9f), new(1, 0.8f), new(2, 0.7f), new(3, 0.6f), new(4, 0.5f)];

        IReadOnlyList<ScoredIndex> diversified = ResultDiversifier.Diversify(
            ranked, _ => "src/OneClass.cs", limit: 3);

        Assert.Equal(3, diversified.Count);
        // Rank order is preserved: the top three by score, not an arbitrary subset.
        Assert.Equal([0, 1, 2], diversified.Select(r => r.Index));
    }

    [Fact]
    public void Diversify_NoCrowding_PreservesOriginalRankOrder()
    {
        ScoredIndex[] ranked = [new(0, 0.9f), new(1, 0.8f), new(2, 0.7f), new(3, 0.6f)];

        IReadOnlyList<ScoredIndex> diversified = ResultDiversifier.Diversify(
            ranked, index => $"src/File{index}.cs", limit: 3);

        Assert.Equal([0, 1, 2], diversified.Select(r => r.Index));
    }

    [Fact]
    public void Diversify_LimitZeroOrNegative_ReturnsEmpty()
    {
        ScoredIndex[] ranked = [new(0, 0.9f)];

        Assert.Empty(ResultDiversifier.Diversify(ranked, _ => "src/A.cs", limit: 0));
        Assert.Empty(ResultDiversifier.Diversify(ranked, _ => "src/A.cs", limit: -1));
    }

    [Fact]
    public void Diversify_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ResultDiversifier.Diversify([], _ => "src/A.cs", limit: 5));
    }

    [Fact]
    public void Diversify_NonPositiveMaxPerFile_Throws()
    {
        ScoredIndex[] ranked = [new(0, 0.9f)];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ResultDiversifier.Diversify(ranked, _ => "src/A.cs", limit: 5, maxPerFile: 0));
    }
}
