using Connapse.Core;
using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

public sealed record Trace(
    TimeSpan Total,
    IReadOnlyDictionary<string, TimeSpan> Stages,
    int? ToolCalls = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public sealed record SearchOutcome(IReadOnlyList<RankedDoc> Ranked, Trace Trace, string? Error);

public sealed record IndexReport(int Documents, int Failed, IReadOnlyList<string> FailedDocumentIds)
{
    /// <summary>One outcome per corpus document, in corpus order; empty for systems that do not report them.</summary>
    public IReadOnlyList<DocumentOutcome> Outcomes { get; init; } = [];
}

/// <summary>
/// Where one document ended up. <paramref name="UploadError"/> is set when the upload was rejected, in
/// which case there is no Connapse document. <paramref name="Status"/> and <paramref name="ErrorMessage"/>
/// are the pipeline's; <paramref name="IngestionState"/> is what the UI badge shows.
/// </summary>
public sealed record DocumentOutcome(
    string DatasetDocId,
    string? ConnapseDocId,
    string? UploadError,
    string? Status,
    string? ErrorMessage,
    IngestionState? IngestionState,
    bool Stalled,
    TimeSpan Elapsed);

public enum IngestionWait
{
    /// <summary>Ranking runs: a container that stops making progress aborts the run.</summary>
    ThrowOnStall,

    /// <summary>Extract runs: documents that do not settle are recorded as stalled.</summary>
    RecordStalls,
}

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
