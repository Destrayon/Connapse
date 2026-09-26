using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

public sealed record Trace(
    TimeSpan Total,
    IReadOnlyDictionary<string, TimeSpan> Stages,
    int? ToolCalls = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public sealed record SearchOutcome(IReadOnlyList<RankedDoc> Ranked, Trace Trace, string? Error);

public sealed record IndexReport(int Documents, int Failed, IReadOnlyList<string> FailedDocumentIds);

/// <summary>
/// A search system the harness can score. One instance per (system, config): settings are applied
/// when the system starts. Future graph, image and agent systems implement this same contract.
/// </summary>
public interface ISystemUnderTest : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Effective model IDs and settings, recorded in the run manifest.</summary>
    IReadOnlyDictionary<string, string> Describe();

    Task<IndexReport> IndexAsync(EvalDataset dataset, CancellationToken ct);

    Task<SearchOutcome> SearchAsync(string dataset, EvalQuery query, int k, CancellationToken ct);
}
