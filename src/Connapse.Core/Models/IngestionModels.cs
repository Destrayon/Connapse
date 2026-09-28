namespace Connapse.Core;

public record IngestionOptions(
    string? DocumentId = null,
    string? FileName = null,
    string? ContentType = null,
    string? ContainerId = null,
    string? Path = null,
    ChunkingStrategy Strategy = ChunkingStrategy.Semantic,
    Dictionary<string, string>? Metadata = null,
    int Generation = 0)
{
    /// <summary>
    /// Who will own the ingested document. Preferred over <see cref="ContainerId"/>, which
    /// cannot express source ownership. When null, ContainerId is used and the owner is
    /// taken to be a container.
    /// </summary>
    public OwnerRef? Owner { get; init; }
}

public record IngestionResult(
    string DocumentId,
    int ChunkCount,
    TimeSpan Duration,
    List<string> Warnings);

public enum ChunkingStrategy
{
    Semantic,
    FixedSize,
    Recursive,
    DocumentAware,
    SentenceWindow,

    /// <summary>
    /// A record with a discussion — an issue or pull request. Chosen by the connector for the
    /// content's shape rather than configured, so it outranks the markdown auto-route and stays
    /// in place when the instance's configured strategy changes.
    /// </summary>
    Record,
}
