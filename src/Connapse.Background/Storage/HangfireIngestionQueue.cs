using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Background.Storage;

/// <summary>
/// Hangfire-backed <see cref="IIngestionQueue"/>. Keeps no state of its own: the document row
/// carries the status and the id of the job that will process it.
/// </summary>
/// <remarks>
/// Singleton, because the sync engine — itself a singleton — holds one; the scoped lifecycle and
/// database context are resolved per call.
/// </remarks>
public sealed class HangfireIngestionQueue(IBackgroundJobClient bgClient, IServiceScopeFactory scopeFactory)
    : IIngestionQueue
{
    public async Task<string?> EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(job.DocumentId, out Guid documentId))
            throw new ArgumentException($"Document id '{job.DocumentId}' is not a GUID.", nameof(job));

        await using var scope = scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IDocumentLifecycle>();

        // Queued first, then the job. The other order lets a fast worker find the document not
        // yet Queued, fail its claim and exit, leaving the document waiting on a job that is gone.
        int? generation = await lifecycle.EnqueuedAsync(documentId, job.ResetAttempts, cancellationToken);
        if (generation is null) return null;

        // The generation rides on the job so the worker can tell it is still the latest. If the
        // enqueue itself throws, the document stays Queued with no job recorded, which is what the
        // stuck-job sweep looks for.
        IngestionOptions options = job.Options with { Generation = generation.Value };
        string jobId = bgClient.Enqueue<IIngestionJobs>(j => j.IngestAsync(job.DocumentId, options, default));

        await lifecycle.RecordJobAsync(documentId, generation.Value, jobId, cancellationToken);
        return jobId;
    }

    public async Task<bool> CancelJobForDocumentAsync(string documentId)
    {
        if (!Guid.TryParse(documentId, out Guid id)) return false;

        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();

        string? jobId = await context.Documents
            .Where(d => d.Id == id)
            .Select(d => d.JobId)
            .FirstOrDefaultAsync();

        return jobId is not null && bgClient.Delete(jobId);
    }

    public async Task<int> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync(cancellationToken);

        return await context.Documents.CountAsync(
            d => d.IngestionStatus == DocumentStatus.Queued || d.IngestionStatus == DocumentStatus.Processing,
            cancellationToken);
    }
}
