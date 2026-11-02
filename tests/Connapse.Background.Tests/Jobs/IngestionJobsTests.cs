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
[Trait("Size", "Medium")]
public class IngestionJobsTests
{
    private readonly IKnowledgeIngester _ingester = Substitute.For<IKnowledgeIngester>();
    private readonly IDocumentStore _docStore = Substitute.For<IDocumentStore>();
    private readonly IDocumentLifecycle _lifecycle = Substitute.For<IDocumentLifecycle>();
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
    }

    private IngestionJobs CreateJobs() => new(
        _ingester, _docStore, _lifecycle, _bgClient,
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
    public async Task IngestAsync_JobEnqueuedBeforeGenerations_ClaimsAndIngestsAtTheCurrentGeneration()
    {
        // Run with 0, the pipeline would read it as a direct caller and could take over a newer upload (CON-102).
        _lifecycle.CurrentGenerationAsync(_documentId, Arg.Any<CancellationToken>()).Returns(5);
        _lifecycle.TryClaimAsync(_documentId, 5, Arg.Any<CancellationToken>()).Returns(true);

        await CreateJobs().IngestAsync(DocumentId, Options(generation: 0), CancellationToken.None);

        await _lifecycle.DidNotReceive().TryClaimAsync(_documentId, 0, Arg.Any<CancellationToken>());
        await _ingester.Received(1).IngestByIdAsync(
            DocumentId, Arg.Is<IngestionOptions>(o => o.Generation == 5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestAsync_JobEnqueuedBeforeGenerationsForADeletedDocument_DoesNotRunThePipeline()
    {
        _lifecycle.CurrentGenerationAsync(_documentId, Arg.Any<CancellationToken>()).Returns((int?)null);

        await CreateJobs().IngestAsync(DocumentId, Options(generation: 0), CancellationToken.None);

        await _lifecycle.DidNotReceiveWithAnyArgs().TryClaimAsync(default, default, default);
        await _ingester.DidNotReceiveWithAnyArgs().IngestByIdAsync(default!, default!, default);
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
}
