using Connapse.Core;
using Connapse.Eval.Model;

namespace Connapse.Eval.Systems;

public sealed record Trace(
    TimeSpan Total,
    IReadOnlyDictionary<string, TimeSpan> Stages,
    int? ToolCalls = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public sealed record SearchOutcome(IReadOnlyList<RankedDoc> Ranked, Trace Trace, string? Error)
{
    /// <summary>Each side's pooled candidates, when the config asks for them (fusion is replayed offline from these).</summary>
    public CandidateCapture? Candidates { get; init; }

    /// <summary>The top chunks in rank order, before collapsing to documents; passage datasets are judged on these.</summary>
    public IReadOnlyList<RetrievedPassage>? Passages { get; init; }
}

/// <summary>One retrieved chunk, with its dataset document ID.</summary>
public sealed record RetrievedPassage(string DocId, string ChunkId, string Content);

/// <summary>
/// One query's hybrid candidates: each side's own top chunks in rank order, every one scored on both
/// sides (null where a side cannot score it). Enough to replay any fusion over pools up to this size.
/// </summary>
public sealed record CandidateCapture(IReadOnlyList<Candidate> Vector, IReadOnlyList<Candidate> Keyword);

public sealed record Candidate(string ChunkId, string DocId, float? VectorScore, float? KeywordScore);

public sealed record IndexReport(int Documents, int Failed, IReadOnlyList<string> FailedDocumentIds)
{
    /// <summary>One outcome per corpus document, in corpus order; empty for systems that do not report them.</summary>
    public IReadOnlyList<DocumentOutcome> Outcomes { get; init; } = [];
}

/// <summary>
/// Where one document ended up. <paramref name="UploadError"/> is set when the upload was rejected, in
/// which case there is no Connapse document. <paramref name="Status"/> and <paramref name="ErrorMessage"/>
/// are what REST reports; <paramref name="IngestionStatus"/> is the document's lifecycle status, which also tells a
/// retryable failure from a permanent one.
/// </summary>
public sealed record DocumentOutcome(
    string DatasetDocId,
    string? ConnapseDocId,
    string? UploadError,
    string? Status,
    string? ErrorMessage,
    DocumentStatus? IngestionStatus,
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

    /// <summary>
    /// Makes <paramref name="config"/>'s search-time settings and search mode the ones the next searches
    /// use, over the index already built (#667). Only called by multi-config runs, whose system was
    /// started with the shared index-time config.
    /// </summary>
    Task UseSearchConfigAsync(SystemConfig config, CancellationToken ct);

    Task<SearchOutcome> SearchAsync(string dataset, EvalQuery query, int k, CancellationToken ct);
}
