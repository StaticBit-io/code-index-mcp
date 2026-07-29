using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

public sealed class InMemorySourceProviderTests
{
    [Fact]
    public async Task StatAsync_FileFromConstructor_StartsAtUnixEpoch()
    {
        InMemorySourceProvider provider = new(new Dictionary<string, string> { ["a.cs"] = "content" });

        SourceFileStat stat = await provider.StatAsync("a.cs", TestContext.Current.CancellationToken);

        Assert.Equal(DateTime.UnixEpoch, stat.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Set_AdvancesOnlyTheModifiedFilesTimestamp()
    {
        InMemorySourceProvider provider = new(new Dictionary<string, string>
        {
            ["a.cs"] = "one",
            ["b.cs"] = "two",
        });

        provider.Set("a.cs", "one changed");

        SourceFileStat statA = await provider.StatAsync("a.cs", TestContext.Current.CancellationToken);
        SourceFileStat statB = await provider.StatAsync("b.cs", TestContext.Current.CancellationToken);

        Assert.True(statA.LastWriteTimeUtc > DateTime.UnixEpoch);
        Assert.Equal(DateTime.UnixEpoch, statB.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Set_OnNewKey_DoesNotThrow()
    {
        InMemorySourceProvider provider = new(new Dictionary<string, string>());

        provider.Set("new.cs", "content");

        SourceFileStat stat = await provider.StatAsync("new.cs", TestContext.Current.CancellationToken);
        Assert.True(stat.LastWriteTimeUtc > DateTime.UnixEpoch);
    }

    [Fact]
    public async Task Set_AfterRemove_IsStrictlyNewerThanBeforeRemoval()
    {
        InMemorySourceProvider provider = new(new Dictionary<string, string> { ["a.cs"] = "one" });
        SourceFileStat before = await provider.StatAsync("a.cs", TestContext.Current.CancellationToken);

        provider.Remove("a.cs");
        provider.Set("a.cs", "one again");

        SourceFileStat after = await provider.StatAsync("a.cs", TestContext.Current.CancellationToken);
        Assert.True(after.LastWriteTimeUtc > before.LastWriteTimeUtc);
    }

    [Fact]
    public async Task Constructor_CopiesInputDictionary_SoCallerMutationsDoNotLeak()
    {
        Dictionary<string, string> source = new() { ["a.cs"] = "one" };
        InMemorySourceProvider provider = new(source);

        source["b.cs"] = "two";

        // "b.cs" was added to the caller's dictionary after construction, not through
        // Set, so the provider must not see it: StatAsync throws for a key it never
        // learned about via its own API, rather than the constructor aliasing the input.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => provider.StatAsync("b.cs", TestContext.Current.CancellationToken));
    }
}
