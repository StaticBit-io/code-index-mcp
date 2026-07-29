using CodeIndex.Core.Storage;
using Xunit;

namespace CodeIndex.Core.Tests.Storage;

public sealed class OverlayRegistryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ci-overlay-registry-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string RegistryPath => Path.Combine(_dir, "overlays", "registry.json");

    [Fact]
    public async Task SaveThenLoad_RoundTripsSlotsAndActiveSlotId()
    {
        OverlayRegistryStore store = new(_dir);
        OverlayRegistryDocument document = OverlayRegistryDocument.Empty(3) with
        {
            Slots =
            [
                new OverlaySlotInfo
                {
                    SlotId = "ov-0",
                    ContentKey = "abc",
                    DeletedPaths = ["src/Old.cs"],
                    CreatedAtUtc = DateTime.UnixEpoch,
                    LastUsedUtc = DateTime.UnixEpoch,
                },
            ],
            ActiveSlotId = "ov-0",
        };

        await store.SaveAsync(document, TestContext.Current.CancellationToken);
        OverlayRegistryDocument loaded = await store.LoadAsync(999, TestContext.Current.CancellationToken);

        Assert.Equal("ov-0", loaded.ActiveSlotId);
        Assert.Single(loaded.Slots);
        Assert.Equal(3, loaded.CompositionGeneration);
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyRegistryWhenTheFileHasNeverBeenCreated()
    {
        OverlayRegistryStore store = new(_dir);

        OverlayRegistryDocument document = await store.LoadAsync(compositionGenerationIfMissing: 7, TestContext.Current.CancellationToken);

        Assert.Empty(document.Slots);
        Assert.Null(document.ActiveSlotId);
        Assert.Equal(7, document.CompositionGeneration);
    }

    [Fact]
    public async Task LoadAsync_DegradesToAnEmptyRegistryWhenTheFileIsMalformedJson()
    {
        // The overlay pool is purely an optimisation over the base index -- a truncated or
        // otherwise malformed registry.json must not take the whole project's search down with
        // it (see OverlayRegistryStore.LoadAsync remarks).
        OverlayRegistryStore store = new(_dir);
        await store.SaveAsync(OverlayRegistryDocument.Empty(1), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(RegistryPath, "{ this is not valid json", TestContext.Current.CancellationToken);

        OverlayRegistryDocument document = await store.LoadAsync(compositionGenerationIfMissing: 5, TestContext.Current.CancellationToken);

        Assert.Empty(document.Slots);
        Assert.Null(document.ActiveSlotId);
        Assert.Equal(5, document.CompositionGeneration);
    }

    [Fact]
    public async Task LoadAsync_DegradesToAnEmptyRegistryWhenARequiredFieldIsMissing()
    {
        // OverlayRegistryDocument's members are `required`; a schema change or a partial write
        // that drops one (here, NextSlotSequence) throws a JsonException just like malformed JSON
        // does, and must degrade to empty the same way rather than propagate.
        OverlayRegistryStore store = new(_dir);
        await store.SaveAsync(OverlayRegistryDocument.Empty(1), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(RegistryPath, "{\"Slots\":[],\"CompositionGeneration\":1}", TestContext.Current.CancellationToken);

        OverlayRegistryDocument document = await store.LoadAsync(compositionGenerationIfMissing: 5, TestContext.Current.CancellationToken);

        Assert.Empty(document.Slots);
        Assert.Equal(5, document.CompositionGeneration);
    }
}
