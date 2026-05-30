namespace Connapse.Core.Interfaces;

public interface IPerDocSummarizer
{
    /// <param name="contentHash">
    /// The document's canonical content hash (the raw-file hash stored as
    /// <c>documents.content_hash</c>). Persisted as <c>summary_content_hash</c> and used to
    /// skip regeneration when unchanged. Callers pass the same value the container rollup's
    /// cache-check compares against, so both the eager and lazy paths invalidate identically.
    /// </param>
    Task<PerDocSummarizationResult> GenerateAsync(
        string documentId,
        string contentHash,
        string docText,
        string? mimeType,
        string fileName,
        SummarySettings settings,
        CancellationToken ct = default);
}

public sealed record PerDocSummarizationResult(
    bool Skipped,
    string? SkipReason = null,
    string? Summary = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? Model = null);
