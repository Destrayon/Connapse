using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Summarization;
using Connapse.Storage.Llm;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Summarization;

[Trait("Category", "Unit")]
public class PerDocSummarizerTests
{
    private static Document MakeDoc(string id, string contentHash, string? existingSummaryHash = null) =>
        new(
            Id: id,
            ContainerId: Guid.NewGuid().ToString(),
            FileName: "test.txt",
            ContentType: "text/plain",
            Path: "/test.txt",
            SizeBytes: 100,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>(),
            Summary: existingSummaryHash != null ? "old summary" : null,
            SummaryGeneratedAt: existingSummaryHash != null ? DateTime.UtcNow : (DateTime?)null,
            SummaryContentHash: existingSummaryHash);

    [Fact]
    public async Task GenerateAsync_SkipsLlm_WhenContentHashUnchanged()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        IDocumentStore docStore = Substitute.For<IDocumentStore>();
        ITokenCounter tokenCounter = Substitute.For<ITokenCounter>();

        string docId = Guid.NewGuid().ToString();
        string docText = "doc text";
        string hash = ComputeSha256(docText);

        docStore.GetAsync(docId, Arg.Any<CancellationToken>())
            .Returns(MakeDoc(docId, hash, existingSummaryHash: hash));

        PerDocSummarizer subject = new(llm, docStore, tokenCounter);
        SummarySettings settings = new() { Enabled = true };
        PerDocSummarizationResult result = await subject.GenerateAsync(docId, docText, "text/plain", "file.txt", settings, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("content_hash_match");
        await llm.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!);
    }

    [Fact]
    public async Task GenerateAsync_CallsLlm_WhenContentChanged()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        IDocumentStore docStore = Substitute.For<IDocumentStore>();
        ITokenCounter tokenCounter = Substitute.For<ITokenCounter>();

        string docId = Guid.NewGuid().ToString();
        docStore.GetAsync(docId, Arg.Any<CancellationToken>())
            .Returns(MakeDoc(docId, "old_hash", existingSummaryHash: null));

        llm.ModelId.Returns("claude-haiku-4-5");
        llm.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns("Apple earnings summary");

        tokenCounter.CountTokens(Arg.Any<string>()).Returns(5100, 100); // input total, output

        PerDocSummarizer subject = new(llm, docStore, tokenCounter);
        SummarySettings settings = new() { Enabled = true };
        PerDocSummarizationResult result = await subject.GenerateAsync(docId, "doc text", "text/plain", "file.txt", settings, CancellationToken.None);

        result.Skipped.Should().BeFalse();
        result.Summary.Should().Be("Apple earnings summary");
        result.Model.Should().Be("claude-haiku-4-5");
        result.OutputTokens.Should().Be(100);

        await docStore.Received(1).UpdateSummaryAsync(
            docId, "Apple earnings summary", Arg.Any<DateTime>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_SkipsLlm_WhenProviderNotConfigured()
    {
        IDocumentStore docStore = Substitute.For<IDocumentStore>();
        ITokenCounter tokenCounter = Substitute.For<ITokenCounter>();

        string docId = Guid.NewGuid().ToString();

        PerDocSummarizer subject = new(llmProvider: null, docStore, tokenCounter);
        SummarySettings settings = new() { Enabled = true };
        PerDocSummarizationResult result = await subject.GenerateAsync(docId, "text", "text/plain", "x.txt", settings, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("no_provider_configured");
        await docStore.DidNotReceiveWithAnyArgs().GetAsync(default!);
    }

    [Fact]
    public async Task GenerateAsync_SkipsLlm_WhenTextIsEmpty()
    {
        ILlmProvider llm = Substitute.For<ILlmProvider>();
        IDocumentStore docStore = Substitute.For<IDocumentStore>();
        ITokenCounter tokenCounter = Substitute.For<ITokenCounter>();

        PerDocSummarizer subject = new(llm, docStore, tokenCounter);
        SummarySettings settings = new() { Enabled = true };
        PerDocSummarizationResult result = await subject.GenerateAsync(
            Guid.NewGuid().ToString(), "  \n\t  ", "text/plain", "empty.txt", settings, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("extraction_empty");
    }

    [Fact]
    public async Task GenerateAsync_WhenEnabledIsFalse_SkipsWithoutCallingProvider()
    {
        var llmProvider = Substitute.For<ILlmProvider>();
        var docStore = Substitute.For<IDocumentStore>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        var summarizer = new PerDocSummarizer(llmProvider, docStore, tokenCounter);
        var settings = new SummarySettings { Enabled = false };

        var result = await summarizer.GenerateAsync(
            "doc-1", "some text", "text/plain", "doc.txt", settings, CancellationToken.None);

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Be("summaries_disabled");
        await llmProvider.DidNotReceive().CompleteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_WithCustomPerDocSystemPrompt_SendsThatPromptToProvider()
    {
        var llmProvider = Substitute.For<ILlmProvider>();
        llmProvider.ModelId.Returns("test-model");
        llmProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("generated summary"));

        var docStore = Substitute.For<IDocumentStore>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.CountTokens(Arg.Any<string>()).Returns(10);

        var summarizer = new PerDocSummarizer(llmProvider, docStore, tokenCounter);
        string customPrompt = "OVERRIDE SYSTEM PROMPT — must reach the LLM verbatim.";
        var settings = new SummarySettings
        {
            Enabled = true,
            PerDocSystemPrompt = customPrompt
        };

        await summarizer.GenerateAsync(
            "doc-1", "some text", "text/plain", "doc.txt", settings, CancellationToken.None);

        await llmProvider.Received(1).CompleteAsync(
            customPrompt,
            Arg.Any<string>(),
            Arg.Any<LlmCompletionOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_WithLlmModelOverride_PassesModelInOptions()
    {
        var llmProvider = Substitute.For<ILlmProvider>();
        llmProvider.ModelId.Returns("default-model");
        llmProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("generated"));

        var docStore = Substitute.For<IDocumentStore>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.CountTokens(Arg.Any<string>()).Returns(10);

        var summarizer = new PerDocSummarizer(llmProvider, docStore, tokenCounter);
        var settings = new SummarySettings { Enabled = true, LlmModel = "qwen3:14b" };

        await summarizer.GenerateAsync(
            "doc-1", "text", "text/plain", "doc.txt", settings, CancellationToken.None);

        await llmProvider.Received(1).CompleteAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<LlmCompletionOptions?>(o => o != null && o.Model == "qwen3:14b"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateAsync_WithMaxInputTokens_TruncatesInputToFourTimesTheLimit()
    {
        var llmProvider = Substitute.For<ILlmProvider>();
        llmProvider.ModelId.Returns("test-model");
        string? capturedUserPrompt = null;
        llmProvider.CompleteAsync(Arg.Any<string>(), Arg.Do<string>(s => capturedUserPrompt = s), Arg.Any<LlmCompletionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("done"));

        var docStore = Substitute.For<IDocumentStore>();
        var tokenCounter = Substitute.For<ITokenCounter>();
        tokenCounter.CountTokens(Arg.Any<string>()).Returns(10);

        var summarizer = new PerDocSummarizer(llmProvider, docStore, tokenCounter);
        string longText = new string('a', 5000);  // 5000 chars
        var settings = new SummarySettings { Enabled = true, MaxInputTokens = 100 };  // 100 tokens × 4 = 400 chars

        await summarizer.GenerateAsync(
            "doc-1", longText, "text/plain", "doc.txt", settings, CancellationToken.None);

        capturedUserPrompt.Should().NotBeNull();
        capturedUserPrompt!.Should().Contain(new string('a', 400));
        capturedUserPrompt.Should().NotContain(new string('a', 401));
    }

    private static string ComputeSha256(string s)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexStringLower(bytes);
    }
}
