namespace Connapse.Application.Ports;

/// <summary>
/// Reads the background jobs an upload queues. Web answers it from Hangfire's job storage, which Connapse.Application
/// doesn't reference.
/// </summary>
public interface IIngestionJobs
{
    /// <summary>The document an ingestion job names, or null when there is no such job, including for an id it couldn't have issued.</summary>
    Task<string?> DocumentIdAsync(string jobId, CancellationToken ct);
}
