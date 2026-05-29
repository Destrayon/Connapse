using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;

namespace Connapse.Ingestion.Summarization;

public sealed class PerDocSummarizer(
    ILlmProvider? llmProvider,
    IDocumentStore docStore,
    ITokenCounter tokenCounter) : IPerDocSummarizer
{
    private const int DefaultMaxInputTokens = 5_000;

    public async Task<PerDocSummarizationResult> GenerateAsync(
        string documentId,
        string contentHash,
        string docText,
        string? mimeType,
        string fileName,
        SummarySettings settings,
        CancellationToken ct = default)
    {
        if (!settings.Enabled)
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "summaries_disabled");

        if (llmProvider is null)
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "no_provider_configured");

        if (string.IsNullOrWhiteSpace(docText))
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "extraction_empty");

        Connapse.Core.Document? existing = await docStore.GetAsync(documentId, ct);
        if (existing?.SummaryContentHash == contentHash)
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "content_hash_match");

        int maxTokens = settings.MaxInputTokens ?? DefaultMaxInputTokens;
        int maxChars = maxTokens * 4;
        bool wasTruncated = docText.Length > maxChars;
        string truncatedText = wasTruncated ? docText[..maxChars] : docText;

        string systemPrompt = !string.IsNullOrWhiteSpace(settings.PerDocSystemPrompt)
            ? settings.PerDocSystemPrompt
            : SummaryPrompts.PerDocSystemPrompt;

        string userMessage = SummaryPrompts.RenderPerDocUserMessage(fileName, mimeType, truncatedText, wasTruncated);

        int inputTokens = tokenCounter.CountTokens(systemPrompt) + tokenCounter.CountTokens(userMessage);

        LlmCompletionOptions? options = settings.LlmModel is null
            ? null
            : new LlmCompletionOptions(Model: settings.LlmModel);

        string responseText = await llmProvider.CompleteAsync(
            systemPrompt, userMessage, options, ct);

        int outputTokens = tokenCounter.CountTokens(responseText);
        string model = settings.LlmModel ?? llmProvider.ModelId;

        DateTime now = DateTime.UtcNow;
        await docStore.UpdateSummaryAsync(documentId, responseText, now, contentHash, ct);

        return new PerDocSummarizationResult(
            Skipped: false,
            Summary: responseText,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            Model: model);
    }
}
