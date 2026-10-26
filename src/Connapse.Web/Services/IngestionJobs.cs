using Connapse.Application.Ports;
using Hangfire;

namespace Connapse.Web.Services;

/// <summary>Answers the ingestion job lookup from Hangfire's job storage.</summary>
public sealed class IngestionJobs(JobStorage jobStorage) : IIngestionJobs
{
    /// <summary>
    /// The job storage refuses an id it couldn't have issued, such as a word or a number too large for the PostgreSQL
    /// storage's, with a FormatException or an OverflowException.
    /// </summary>
    public Task<string?> DocumentIdAsync(string jobId, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(jobStorage.GetMonitoringApi().JobDetails(jobId)?.Job?.Args.FirstOrDefault() as string);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
