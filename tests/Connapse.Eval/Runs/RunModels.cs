using Connapse.Core;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Runs;

public sealed record RunDatasetInfo(
    string Name,
    string Version,
    IReadOnlyDictionary<string, string> FileSha256,
    IReadOnlyList<string> Tags,
    int Documents,
    int FailedDocuments,
    bool Invalid,
    int Queries);

public sealed record RunManifest(
    string GitSha,
    bool GitDirty,
    string Suite,
    string System,
    string Config,
    string ConfigHash,
    SearchMode SearchMode,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> SystemDescription,
    string Machine,
    string Os,
    int ProcessorCount,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    IReadOnlyList<RunDatasetInfo> Datasets);

public sealed record QueryResult(
    string Dataset,
    string QueryId,
    string QueryText,
    Split Split,
    IReadOnlyList<RankedDoc> Ranked,
    Trace Trace,
    string? Error);
