using AIKnowledge.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AIKnowledge.Core;

namespace AIKnowledge.Ingestion.Pipeline;

/// <summary>
/// Background service that processes ingestion jobs from the queue.
/// Supports parallel processing of multiple jobs.
/// </summary>
public class IngestionWorker : BackgroundService
{
    private readonly IIngestionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly IOptionsMonitor<UploadSettings> _uploadSettings;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IIngestionQueue queue,
        IServiceScopeFactory scopeFactory,
        IKnowledgeFileSystem fileSystem,
        IOptionsMonitor<UploadSettings> uploadSettings,
        ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _fileSystem = fileSystem;
        _uploadSettings = uploadSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IngestionWorker started");

        var parallelWorkers = _uploadSettings.CurrentValue.ParallelWorkers;

        // Start multiple worker tasks
        var workers = Enumerable.Range(0, parallelWorkers)
            .Select(i => ProcessJobsAsync(i, stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "IngestionWorker encountered an error");
        }

        _logger.LogInformation("IngestionWorker stopped");
    }

    private async Task ProcessJobsAsync(int workerId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker {WorkerId} started", workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Dequeue next job (blocks until available or cancelled)
                var job = await _queue.DequeueAsync(stoppingToken);

                if (job == null)
                {
                    continue;
                }

                _logger.LogInformation(
                    "Worker {WorkerId} processing job {JobId} for document {DocumentId}",
                    workerId,
                    job.JobId,
                    job.DocumentId);

                // Update status to Processing
                if (_queue is IngestionQueue queue)
                {
                    queue.UpdateJobStatus(
                        job.JobId,
                        IngestionJobState.Processing,
                        IngestionPhase.Parsing,
                        0);
                }

                // Create a per-job CTS linked with the application stopping token
                // so the job can be individually cancelled (e.g., on document delete)
                using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                if (_queue is IngestionQueue q)
                    q.RegisterJobCancellation(job.JobId, jobCts);

                IngestionResult result;
                try
                {
                    result = await ProcessJobAsync(job, jobCts.Token);
                }
                finally
                {
                    if (_queue is IngestionQueue q2)
                        q2.UnregisterJobCancellation(job.JobId);
                }

                // Update final status
                if (_queue is IngestionQueue queue2)
                {
                    if (result.Warnings.Any(w => w.Contains("failed", StringComparison.OrdinalIgnoreCase)))
                    {
                        queue2.UpdateJobStatus(
                            job.JobId,
                            IngestionJobState.Failed,
                            IngestionPhase.Complete,
                            100,
                            string.Join("; ", result.Warnings));
                    }
                    else
                    {
                        queue2.UpdateJobStatus(
                            job.JobId,
                            IngestionJobState.Completed,
                            IngestionPhase.Complete,
                            100);
                    }
                }

                _logger.LogInformation(
                    "Worker {WorkerId} completed job {JobId}: {ChunkCount} chunks in {Duration}ms",
                    workerId,
                    job.JobId,
                    result.ChunkCount,
                    result.Duration.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Application is shutting down
                break;
            }
            catch (OperationCanceledException)
            {
                // Per-job cancellation (e.g., document was deleted) — continue to next job
                _logger.LogInformation("Worker {WorkerId}: job was cancelled (document deleted)", workerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} encountered an error processing job", workerId);
            }
        }

        _logger.LogInformation("Worker {WorkerId} stopped", workerId);
    }

    private async Task<IngestionResult> ProcessJobAsync(IngestionJob job, CancellationToken ct)
    {
        try
        {
            // Create a scope for scoped dependencies (IKnowledgeIngester uses DbContext)
            await using var scope = _scopeFactory.CreateAsyncScope();
            var ingester = scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>();

            // Open file from storage
            using var fileStream = await _fileSystem.OpenFileAsync(job.Path, ct);

            // Process through ingestion pipeline
            var result = await ingester.IngestAsync(fileStream, job.Options, ct);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing job {JobId} for document {DocumentId}",
                job.JobId,
                job.DocumentId);

            return new IngestionResult(
                DocumentId: job.DocumentId,
                ChunkCount: 0,
                Duration: TimeSpan.Zero,
                Warnings: [$"Processing failed: {ex.Message}"]);
        }
    }
}
