namespace Connapse.Core;

/// <summary>
/// A file's bytes plus metadata the connector learned only by reading it, such as a Jira issue's links, which its
/// listing does not carry (CON-34). The pipeline merges <see cref="Metadata"/> into the job's metadata before it
/// stages links. Only namespaced keys (<c>jira:…</c>) are taken, so a read cannot overwrite the sync's own keys,
/// such as the remote signature.
/// </summary>
public sealed class MetadataStream(byte[] content, IReadOnlyDictionary<string, string> metadata)
    : MemoryStream(content, writable: false)
{
    public IReadOnlyDictionary<string, string> Metadata { get; } = metadata;

    /// <summary>
    /// <paramref name="options"/> with what <paramref name="stream"/> learned merged into its metadata, replacing what
    /// the job carried under the same keys. A connector reports every key it owns, empty when there is nothing, so a
    /// link removed since a previous ingest does not linger in metadata a reindex carried forward.
    /// </summary>
    public static IngestionOptions Merge(IngestionOptions options, Stream stream)
    {
        if (stream is not MetadataStream { Metadata.Count: > 0 } read)
            return options;

        Dictionary<string, string> merged = new(options.Metadata ?? [], StringComparer.Ordinal);
        foreach ((string key, string value) in read.Metadata)
        {
            if (key.IndexOf(':') > 0)
                merged[key] = value;
        }
        return options with { Metadata = merged };
    }
}
