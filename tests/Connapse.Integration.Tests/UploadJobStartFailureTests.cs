using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Background.Jobs;
using Connapse.Background.Storage;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// An upload whose indexing job fails to start is not left Queued for good (CON-96). The upload runs against the
/// real stores and queue; only Hangfire's job creation is made to fail, after the document is marked Queued.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UploadJobStartFailureTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Upload_JobFailsToStart_TheSweepStartsItsJob()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            var result = await UploadWithFailingJobCreationAsync(containerId, strategy: "FixedSize");

            result.Success.Should().BeTrue("the file and its record are stored");
            result.JobId.Should().BeNull();
            var before = await DocumentAsync(result.DocumentId!);
            before.IngestionStatus.Should().Be(DocumentStatus.Queued);
            before.JobId.Should().BeNull();

            await AgeStatusAsync(result.DocumentId!, TimeSpan.FromMinutes(6));
            await RunSweepAsync();

            var after = await DocumentAsync(result.DocumentId!);
            after.JobId.Should().NotBeNull("the sweep enqueues the job the upload could not");
            after.Generation.Should().BeGreaterThan(before.Generation);
            // The recovered job indexes the file the way its uploader asked.
            using var connection = fixture.Factory.Services.GetRequiredService<JobStorage>().GetConnection();
            var options = connection.GetJobData(after.JobId!)!.Job.Args[1].Should().BeOfType<IngestionOptions>().Subject;
            options.Strategy.Should().Be(ChunkingStrategy.FixedSize);
            options.StrategyIsExplicit.Should().BeTrue();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task Sweep_QueuedWithNoJobForUnderFiveMinutes_LeavesIt()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            var result = await UploadWithFailingJobCreationAsync(containerId);
            await AgeStatusAsync(result.DocumentId!, TimeSpan.FromMinutes(1));

            await RunSweepAsync();

            (await DocumentAsync(result.DocumentId!)).JobId.Should().BeNull("its job may still be about to be recorded");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task Sweep_QueuedWithALiveJob_LeavesIt()
    {
        var containerId = await CreateContainerAsync();
        string? scheduled = null;
        try
        {
            var result = await UploadWithFailingJobCreationAsync(containerId);
            var documentId = Guid.Parse(result.DocumentId!);
            var stored = await DocumentAsync(result.DocumentId!);

            // A retry waiting its turn: Queued, with a job that has not run yet.
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var client = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
                scheduled = client.Schedule<IIngestionJobs>(j => j.IngestAsync(result.DocumentId!, null!, default), TimeSpan.FromHours(1));
                await scope.ServiceProvider.GetRequiredService<IDocumentLifecycle>()
                    .RecordJobAsync(documentId, stored.Generation, scheduled);
            }
            await AgeStatusAsync(result.DocumentId!, TimeSpan.FromMinutes(6));

            await RunSweepAsync();

            var after = await DocumentAsync(result.DocumentId!);
            after.JobId.Should().Be(scheduled);
            after.Generation.Should().Be(stored.Generation);
        }
        finally
        {
            if (scheduled is not null)
                fixture.Factory.Services.GetRequiredService<IBackgroundJobClient>().Delete(scheduled);
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    private async Task<UploadResult> UploadWithFailingJobCreationAsync(string containerId, string? strategy = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var failingClient = Substitute.For<IBackgroundJobClient>();
        failingClient.Create(Arg.Any<Job>(), Arg.Any<IState>())
            .Returns<string>(_ => throw new InvalidOperationException("job storage unavailable"));
        var uploads = new UploadService(
            services.GetRequiredService<IContainerStore>(),
            services.GetRequiredService<IManagedStorageProvider>(),
            services.GetRequiredService<IFolderStore>(),
            new HangfireIngestionQueue(failingClient, services.GetRequiredService<IServiceScopeFactory>()),
            services.GetRequiredService<IDocumentStore>(),
            services.GetRequiredService<IFileTypeValidator>(),
            services.GetRequiredService<IAuditLogger>(),
            NullLogger<UploadService>.Instance);

        return await uploads.UploadAsync(new UploadRequest(
            Guid.Parse(containerId), "stranded.txt", new MemoryStream(Encoding.UTF8.GetBytes("waiting for a job")),
            Guid.NewGuid(), null, "text/plain", strategy, IngestedVia: "API"));
    }

    private async Task RunSweepAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IIngestionJobs>().RequeueStuckDocumentsAsync(CancellationToken.None);
    }

    private async Task AgeStatusAsync(string documentId, TimeSpan age)
    {
        var id = Guid.Parse(documentId);
        DateTime changedAt = DateTime.UtcNow - age;
        using var scope = fixture.Factory.Services.CreateScope();
        await using var context = await ContextAsync(scope);
        await context.Database.ExecuteSqlAsync($"UPDATE documents SET status_changed_at = {changedAt} WHERE id = {id}");
    }

    private async Task<Storage.Data.Entities.DocumentEntity> DocumentAsync(string documentId)
    {
        var id = Guid.Parse(documentId);
        using var scope = fixture.Factory.Services.CreateScope();
        await using var context = await ContextAsync(scope);
        return await context.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
    }

    private static Task<KnowledgeDbContext> ContextAsync(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"jobfail-{Guid.NewGuid():N}"[..20] });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    private record ContainerDto(string Id, string Name);
}
