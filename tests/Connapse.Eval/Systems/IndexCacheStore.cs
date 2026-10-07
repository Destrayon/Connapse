using System.Text.Json;
using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

/// <summary>
/// Where a cached index lives (#672): its database dump in a Docker volume named after the key, and
/// the harness's view of each dataset in it (container, document IDs, index report) in a JSON file
/// under <see cref="Root"/>. A volume survives the throwaway containers; the JSON says it is usable.
/// </summary>
public sealed record IndexCacheStore(string Root, string Key)
{
    public string VolumeName => $"connapse-eval-index-{Key}";

    public string MetadataPath => Path.Combine(Root, Key + ".json");

    public IndexCacheMetadata? Load() =>
        File.Exists(MetadataPath)
            ? JsonSerializer.Deserialize<IndexCacheMetadata>(File.ReadAllText(MetadataPath), EvalJson.Options)
            : null;

    public void Save(IndexCacheMetadata metadata)
    {
        Directory.CreateDirectory(Root);
        string temporary = MetadataPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, EvalJson.Options));
        File.Move(temporary, MetadataPath, overwrite: true);
    }
}

/// <param name="Generation">The snapshot generation in the volume this metadata belongs to: written only after it is complete.</param>
/// <param name="Description">The index-shaping part of the system's description when it was saved; a restore must match it.</param>
public sealed record IndexCacheMetadata(
    string Key,
    string Generation,
    IReadOnlyDictionary<string, string> Description,
    IReadOnlyDictionary<string, CachedDataset> Datasets);

public sealed record CachedDataset(Guid ContainerId, IReadOnlyDictionary<string, string> DocMap, IndexReport Report);
