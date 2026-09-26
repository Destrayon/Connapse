using System.Text.Json;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

public sealed record DatasetFile(string Name, string Url, string? Sha256);

public sealed record DatasetEntry(
    string Adapter,
    string Version,
    IReadOnlyList<string> Tags,
    IReadOnlyList<DatasetFile> Files);

public sealed record EvalManifest(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Suites,
    IReadOnlyDictionary<string, DatasetEntry> Datasets)
{
    public static EvalManifest Load(string path) =>
        JsonSerializer.Deserialize<EvalManifest>(File.ReadAllText(path), EvalJson.Options)
        ?? throw new InvalidOperationException($"{path} is empty.");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, EvalJson.Options) + "\n");

    public IReadOnlyList<string> ResolveSuite(string suite, IReadOnlyCollection<string>? only)
    {
        if (!Suites.TryGetValue(suite, out IReadOnlyList<string>? names))
            throw new ArgumentException($"Unknown suite '{suite}'. Known suites: {string.Join(", ", Suites.Keys)}.");

        if (only is { Count: > 0 })
        {
            string[] strangers = only.Where(o => !names.Contains(o)).ToArray();
            if (strangers.Length > 0)
                throw new ArgumentException($"Datasets not in suite '{suite}': {string.Join(", ", strangers)}.");
            names = names.Where(only.Contains).ToList();
        }

        foreach (string name in names)
            if (!Datasets.ContainsKey(name))
                throw new InvalidOperationException($"Suite '{suite}' lists '{name}', which has no dataset entry.");
        return names;
    }

    public EvalManifest WithPinnedHashes(string dataset, IReadOnlyDictionary<string, string> hashes)
    {
        DatasetEntry entry = Datasets[dataset];
        DatasetEntry pinned = entry with
        {
            Files = entry.Files.Select(f => f with { Sha256 = f.Sha256 ?? hashes[f.Name] }).ToList(),
        };
        Dictionary<string, DatasetEntry> datasets = new(Datasets) { [dataset] = pinned };
        return this with { Datasets = datasets };
    }
}
