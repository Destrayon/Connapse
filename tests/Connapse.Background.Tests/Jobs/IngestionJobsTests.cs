using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class IngestionJobsTests
{
    private readonly IKnowledgeIngester _ingester = Substitute.For<IKnowledgeIngester>();
    private readonly IDocumentStore _docStore = Substitute.For<IDocumentStore>();
    private readonly IDocumentLifecycle _lifecycle = Substitute.For<IDocumentLifecycle>();
    private readonly IPerDocSummarizer _summarizer = Substitute.For<IPerDocSummarizer>();
    private readonly IContainerSettingsResolver _settings = Substitute.For<IContainerSettingsResolver>();
    private readonly IBackgroundJobClient _bgClient = Substitute.For<IBackgroundJobClient>();

    private readonly Guid _containerId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private string DocumentId => _documentId.ToString();

    public IngestionJobsTests()
    {
        _lifecycle.TryClaimAsync(_documentId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _lifecycle.RetryScheduledAsync(_documentId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _ingester.IngestByIdAsync(DocumentId, Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(DocumentId, ChunkCount: 3, TimeSpan.FromMilliseconds(5), []));
        _bgClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("retry-job");
        UseSummaryMethod(enabled: false, SummaryStrategy.DocumentClustering);
    }

    private IngestionJobs CreateJobs() => new(
        _ingester, _docStore, _lifecycle, _summarizer, _settings, _bgClient,
        Substitute.For<IDbContextFactory<KnowledgeDbContext>>(),
        Substitute.For<JobStorage>(),
        Substitute.For<IReindexService>(),
        Substitute.For<ILogger<IngestionJobs>>());

    private IngestionOptions Options(int generation = 7) => new(
        DocumentId: DocumentId,
        FileName: "test.txt",
        ContentType: "text/plain",
        ContainerId: _containerId.ToString(),
        Generation: generation);

    private void UseSummaryMethod(bool enabled, string method) =>
        _settings.GetSummarySettingsAsync(_containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings { Enabled = enabled, ContainerSummaryMethod = method });

    private void DocumentHasAttempts(int attempts) =>
        _docStore.GetAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Document(
                DocumentId, _containerId.ToString(), "test.txt", "text/plain", "/test.txt",
                SizeBytes: 12, CreatedAt: DateTime.UtcNow, Metadata: new Dictionary<string, string>())
            {
                AttemptCount = attempts,
            });

    [Fact]
    public async Task IngestAsync_ClaimRefused_DoesNotRunThePipeline()
    {
        _lifecycle.TryClaimAsync(_documentId, 7, Arg.Any<CancellationToken>()).Returns(false);

        await CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await _ingester.DidNotReceiveWithAnyArgs().IngestByIdAsync(default!, default!, default);
    }

    [Fact]
    public async Task IngestAsync_SummaryClusteringMode_MarksSummaryPendingAndEnqueuesIt()
    {
        UseSummaryMethod(enabled: true, SummaryStrategy.SummaryClustering);

        await CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await _lifecycle.Received(1).SetSummaryStatusAsync(_documentId, SummaryStatus.Pending, Arg.Any<CancellationToken>());
        _bgClient.Received(1).Create(
            Arg.Is<Job>(j => j.Method.Name == nameof(IIngestionJobs.PerDocSummaryAsync)),
            Arg.Any<EnqueuedState>());
    }

    [Fact]
    public async Task IngestAsync_DocumentClusteringMode_NeedsNoPerDocSummary()
    {
        UseSummaryMethod(enabled: true, SummaryStrategy.DocumentClustering);

        await CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await _lifecycle.Received(1).SetSummaryStatusAsync(_documentId, SummaryStatus.NotNeeded, Arg.Any<CancellationToken>());
        _bgClient.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task IngestAsync_PermanentFailure_MarksFailedPermanentWithoutRetrying()
    {
        _ingester.IngestByIdAsync(DocumentId, Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PermanentIngestionException("No extractable content"));

        Func<Task> act = () => CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _lifecycle.Received(1).FailAsync(
            _documentId, 7, DocumentStatus.FailedPermanent, "No extractable content", Arg.Any<CancellationToken>());
        _bgClient.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task IngestAsync_TransientFailureWithAttemptsLeft_RequeuesAndSchedulesARetry()
    {
        DocumentHasAttempts(1);
        _ingester.IngestByIdAsync(DocumentId, Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("embedding provider unreachable"));

        Func<Task> act = () => CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        await _lifecycle.Received(1).RetryScheduledAsync(
            _documentId, 7, "embedding provider unreachable", Arg.Any<CancellationToken>());
        _bgClient.Received(1).Create(
            Arg.Is<Job>(j => j.Method.Name == nameof(IIngestionJobs.IngestAsync)),
            Arg.Is<ScheduledState>(s => s.EnqueueAt > DateTime.UtcNow));
        await _lifecycle.Received(1).RecordJobAsync(_documentId, 7, "retry-job", Arg.Any<CancellationToken>());
        await _lifecycle.DidNotReceiveWithAnyArgs().FailAsync(default, default, default, default!, default);
    }

    [Fact]
    public async Task IngestAsync_TransientFailureOnLastAttempt_MarksFailedRetryable()
    {
        DocumentHasAttempts(IngestionJobs.MaxAttempts);
        _ingester.IngestByIdAsync(DocumentId, Arg.Any<IngestionOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("still down"));

        Func<Task> act = () => CreateJobs().IngestAsync(DocumentId, Options(), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        await _lifecycle.Received(1).FailAsync(
            _documentId, 7, DocumentStatus.FailedRetryable, "still down", Arg.Any<CancellationToken>());
        _bgClient.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task PerDocSummaryAsync_SummaryGenerated_SummarizesTheStoredTextAndMarksDone()
    {
        UseSummaryMethod(enabled: true, SummaryStrategy.SummaryClustering);
        DocumentHasAttempts(0);
        _docStore.GetDocumentTextAsync(DocumentId, Arg.Any<CancellationToken>()).Returns("Text from chunks");
        _summarizer.GenerateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<SummarySettings>(), Arg.Any<CancellationToken>())
            .Returns(new PerDocSummarizationResult(
                Skipped: false, Summary: "Test summary", InputTokens: 10, OutputTokens: 5, Model: "test"));

        await CreateJobs().PerDocSummaryAsync(DocumentId, CancellationToken.None);

        await _summarizer.Received(1).GenerateAsync(
            DocumentId, Arg.Any<string>(), "Text from chunks", Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<SummarySettings>(), Arg.Any<CancellationToken>());
        await _lifecycle.Received(1).SetSummaryStatusAsync(_documentId, SummaryStatus.Done, Arg.Any<CancellationToken>());

        // Rollup is not scheduled per document: the recurring sweep coalesces a burst into one.
        _bgClient.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Theory]
    [InlineData(false, SummaryStrategy.SummaryClustering)]
    [InlineData(true, SummaryStrategy.DocumentClustering)]
    public async Task PerDocSummaryAsync_NoPerDocSummaryDue_SkipsTheSummarizer(bool enabled, string method)
    {
        UseSummaryMethod(enabled, method);
        DocumentHasAttempts(0);

        await CreateJobs().PerDocSummaryAsync(DocumentId, CancellationToken.None);

        await _summarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default!, default);
        await _lifecycle.Received(1).SetSummaryStatusAsync(_documentId, SummaryStatus.NotNeeded, Arg.Any<CancellationToken>());
    }
}
