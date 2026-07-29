using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeIndex.Core.Storage;

/// <summary>
/// Persists the small <see cref="OverlayRegistryDocument"/> at <c>&lt;cacheDirectory&gt;/overlays/registry.json</c>,
/// and owns deleting overlay slot directories (eviction, or wiping the whole pool on a full
/// rebuild). One of the two types outside <c>CodeIndex.Core.Sources</c> allowed to touch
/// <see cref="File"/>/<see cref="Directory"/> directly — see the class remarks on
/// <c>SourceIsolationTests.CoreAssembly_TouchesFileSystemOnlyThroughSourceProviderOrIndexStore</c>.
/// An overlay slot's own chunk/fingerprint/vector data is deliberately not this type's concern —
/// that goes through a plain <see cref="IndexStore"/> pointed at the slot's own directory, reusing
/// the existing, already-tested manifest+vectors format instead of inventing a second one.
/// </summary>
public sealed class OverlayRegistryStore
{
    private const string RegistryFileName = "registry.json";
    private const string TempSuffix = ".tmp";
    private const string OverlaysDirectoryName = "overlays";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _overlaysDirectory;

    public OverlayRegistryStore(string cacheDirectory)
    {
        _overlaysDirectory = Path.Combine(cacheDirectory, OverlaysDirectoryName);
    }

    /// <summary>The directory a given slot's own <see cref="IndexStore"/> should be rooted at.</summary>
    public string SlotDirectory(string slotId) => Path.Combine(_overlaysDirectory, slotId);

    /// <summary>
    /// Loads the registry, or an empty one (see <see cref="OverlayRegistryDocument.Empty"/>) when
    /// <c>overlays/</c> has never been created — the common case for a project that has never
    /// diverged from its base, where this is a single, cheap <see cref="File.Exists(string)"/>
    /// check and nothing more.
    /// </summary>
    public async Task<OverlayRegistryDocument> LoadAsync(int compositionGenerationIfMissing, CancellationToken cancellationToken)
    {
        string path = RegistryPath;
        if (!File.Exists(path))
        {
            return OverlayRegistryDocument.Empty(compositionGenerationIfMissing);
        }

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                OverlayRegistryDocument? document = await JsonSerializer
                    .DeserializeAsync<OverlayRegistryDocument>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                return document ?? OverlayRegistryDocument.Empty(compositionGenerationIfMissing);
            }
            catch (JsonException)
            {
                // The overlay pool is purely an optimisation over the base index (see the class
                // remarks): a truncated or schema-incompatible registry.json must degrade to "no
                // overlays cached" rather than take the whole project's search down with it, the
                // same way IndexStore.LoadAsync degrades a corrupted base index.
                return OverlayRegistryDocument.Empty(compositionGenerationIfMissing);
            }
        }
    }

    /// <summary>Atomic temp-file-then-rename write, same pattern as <see cref="IndexStore.SaveAsync"/>.</summary>
    public async Task SaveAsync(OverlayRegistryDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_overlaysDirectory);

        string tempPath = RegistryPath + TempSuffix;

        FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, RegistryPath, overwrite: true);
    }

    /// <summary>Deletes one evicted or superseded slot's entire directory (its <see
    /// cref="IndexStore"/>-managed manifest/vectors included). Safe to call when the directory
    /// does not exist.</summary>
    public void DeleteSlot(string slotId)
    {
        string path = SlotDirectory(slotId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Wipes the entire overlay pool — every slot and the registry itself — because a
    /// full rebuild replaces base wholesale and every existing overlay's diff was computed
    /// against the old one (see the design doc's decision on whether base ever moves). Safe to
    /// call when <c>overlays/</c> does not exist.</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(_overlaysDirectory))
        {
            Directory.Delete(_overlaysDirectory, recursive: true);
        }
    }

    private string RegistryPath => Path.Combine(_overlaysDirectory, RegistryFileName);
}
