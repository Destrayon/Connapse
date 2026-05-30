using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;

namespace Connapse.Ingestion.Summarization;

public sealed class ContainerSummarizer(
    ILlmProvider? llmProvider,
    ITokenCounter tokenCounter) : IContainerSummarizer
{
    private const int StuffThreshold = 30;
    private const int MaxClusters = 20;

    public async Task<ContainerSummarizationResult> GenerateAsync(
        string containerName,
        IReadOnlyList<DocumentWithSummary> docs,
        SummarySettings settings,
        CancellationToken ct = default)
    {
        if (!settings.Enabled)
            return new ContainerSummarizationResult(true, SkipReason: "summaries_disabled");

        if (llmProvider is null)
            return new ContainerSummarizationResult(true, SkipReason: "no_provider_configured");

        if (docs.Count == 0)
            return new ContainerSummarizationResult(true, SkipReason: "no_documents");

        bool isClustered = docs.Count > StuffThreshold;
        IEnumerable<string> renderedSummaries;
        int? k = null;

        if (!isClustered)
        {
            renderedSummaries = docs.Select(d => d.Summary);
        }
        else
        {
            k = Math.Min(MaxClusters, (int)Math.Ceiling(docs.Count / 3.0));
            var input = docs.Select(d => (d.Id, d.Embedding)).ToList();
            MedoidSelector.SelectionResult selection =
                MedoidSelector.SelectFarthestFirstWithAssignments(input, k.Value);

            Dictionary<Guid, DocumentWithSummary> docsById = docs.ToDictionary(d => d.Id);
            renderedSummaries = selection.Medoids.Select(m =>
            {
                DocumentWithSummary medoidDoc = docsById[m.Id];
                return $"(represents {m.ClusterSize} similar docs): {medoidDoc.Summary}";
            });
        }

        string userMsg = SummaryPrompts.RenderContainerRollupUserMessage(
            containerName: containerName,
            totalDocs: docs.Count,
            isClustered: isClustered,
            summaries: renderedSummaries);

        string systemPrompt = !string.IsNullOrWhiteSpace(settings.ContainerRollupSystemPrompt)
            ? settings.ContainerRollupSystemPrompt
            : SummaryPrompts.ContainerRollupSystemPrompt;

        int inputTokens = tokenCounter.CountTokens(systemPrompt) + tokenCounter.CountTokens(userMsg);

        LlmCompletionOptions? options = string.IsNullOrWhiteSpace(settings.LlmModel)
            ? null
            : new LlmCompletionOptions(Model: settings.LlmModel);

        string responseText = await llmProvider.CompleteAsync(
            systemPrompt, userMsg, options, ct);

        int outputTokens = tokenCounter.CountTokens(responseText);
        string model = string.IsNullOrWhiteSpace(settings.LlmModel) ? llmProvider.ModelId : settings.LlmModel;

        return new ContainerSummarizationResult(
            Skipped: false,
            Summary: responseText,
            Regime: isClustered ? "cluster" : "stuff",
            NumDocs: docs.Count,
            KClusters: k,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            Model: model);
    }
}
