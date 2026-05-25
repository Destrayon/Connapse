namespace Connapse.Core.Interfaces;

public interface IPerDocSummarizer
{
    Task<PerDocSummarizationResult> GenerateAsync(
        string documentId,
        string docText,
        string? mimeType,
        string fileName,
        CancellationToken ct = default);
}

public sealed record PerDocSummarizationResult(
    bool Skipped,
    string? SkipReason = null,
    string? Summary = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? Model = null,
    decimal CostEstimateUsd = 0);
