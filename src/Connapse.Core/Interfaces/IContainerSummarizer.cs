namespace Connapse.Core.Interfaces;

public sealed record DocumentWithSummary(Guid Id, string Summary, float[] Embedding);

public sealed record ContainerSummarizationResult(
    bool Skipped,
    string? SkipReason = null,
    string? Summary = null,
    string? Regime = null,           // "stuff" or "cluster"
    int NumDocs = 0,
    int? KClusters = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    string? Model = null);

public interface IContainerSummarizer
{
    Task<ContainerSummarizationResult> GenerateAsync(
        string containerName,
        IReadOnlyList<DocumentWithSummary> docs,
        CancellationToken ct = default);
}
