using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Llm;

namespace Connapse.Ingestion.Summarization;

public sealed class PerDocSummarizer(
    ILlmProvider? llmProvider,
    IDocumentStore docStore,
    ITokenCounter tokenCounter) : IPerDocSummarizer
{
    private const int MaxInputCharacters = 20_000; // ~5K tokens — configurable later

    public async Task<PerDocSummarizationResult> GenerateAsync(
        string documentId,
        string docText,
        string? mimeType,
        string fileName,
        CancellationToken ct = default)
    {
        if (llmProvider is null)
        {
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "no_provider_configured");
        }

        if (string.IsNullOrWhiteSpace(docText))
        {
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "extraction_empty");
        }

        string contentHash = HexHash.Sha256(docText);

        Connapse.Core.Document? existing = await docStore.GetAsync(documentId, ct);
        if (existing?.SummaryContentHash == contentHash)
        {
            return new PerDocSummarizationResult(Skipped: true, SkipReason: "content_hash_match");
        }

        string truncated = docText.Length > MaxInputCharacters
            ? docText[..MaxInputCharacters]
            : docText;

        string userMessage = SummaryPrompts.RenderPerDocUserMessage(fileName, mimeType, truncated);
        string systemPrompt = SummaryPrompts.PerDocSystemPrompt;

        int inputTokens = tokenCounter.CountTokens(systemPrompt) + tokenCounter.CountTokens(userMessage);

        string responseText = await llmProvider.CompleteAsync(
            systemPrompt, userMessage, options: null, ct);

        int outputTokens = tokenCounter.CountTokens(responseText);
        string model = llmProvider.ModelId;

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
