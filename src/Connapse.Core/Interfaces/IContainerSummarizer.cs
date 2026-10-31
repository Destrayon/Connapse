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
    /// <param name="totalDocuments">
    /// How many documents the container really holds, when <paramref name="docs"/> is a sample of them.
    /// Null means <paramref name="docs"/> is every document.
    /// </param>
    Task<ContainerSummarizationResult> GenerateAsync(
        string containerName,
        IReadOnlyList<DocumentWithSummary> docs,
        SummarySettings settings,
        int? totalDocuments = null,
        CancellationToken ct = default);
}
