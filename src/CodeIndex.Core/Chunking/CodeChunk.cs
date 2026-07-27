using System.Text.Json.Serialization;

namespace CodeIndex.Core.Chunking;

public enum ChunkKind
{
    Unknown = 0,
    Class,
    Interface,
    Struct,
    Record,
    Enum,
    Method,
    Constructor,
    Property,
    Field,
    FileFragment,
}

/// <summary>
/// One indexable unit — a type declaration header or a single member. Bodies are NOT
/// stored here; they are read from the source provider on demand at query time.
/// </summary>
public sealed record CodeChunk
{
    public required string FilePath { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required ChunkKind Kind { get; init; }

    /// <summary>Fully-qualified name, e.g. <c>Acme.Payments.PaymentGateway.Charge</c>.</summary>
    public required string Symbol { get; init; }

    public required string Signature { get; init; }
    public string DocComment { get; init; } = string.Empty;

    /// <summary>
    /// Text handed to the embedding model. Structure carries as much signal as the body.
    /// Deliberately excluded from the persisted manifest (<see cref="JsonIgnoreAttribute"/>):
    /// it is only ever needed at the moment a chunk is embedded, never read back for search or
    /// display, and at project scale it was measured to be the majority of the manifest's size.
    /// Not <c>required</c> — <c>System.Text.Json</c> cannot satisfy a required, reflection-based
    /// member that is also ignored (it throws at start-up, since an ignored property can never
    /// be set from JSON), so this relies on callers setting it via the object initializer and on
    /// the empty-string default for the deserialisation path. Round-tripped chunks always come
    /// back with an empty <see cref="EmbedText"/>.
    /// </summary>
    [JsonIgnore]
    public string EmbedText { get; init; } = string.Empty;
}
