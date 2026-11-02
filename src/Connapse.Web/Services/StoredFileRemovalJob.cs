using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Hangfire;

namespace Connapse.Web.Services;

/// <summary>
/// Drains the stored files owed a removal after a folder delete (CON-100). Each entry is retried with growing waits
/// until its file is gone; one still failing a day after it was queued is logged as an error and keeps being retried.
/// A file is removed only while no record points to its path, so one a newer upload wrote there stays, and the entry
/// is done.
/// </summary>
public sealed class StoredFileRemovalJob(
    IStoredFileRemovalStore removals,
    IDocumentStore documents,
    IManagedStorageProvider storage,
    TimeProvider time,
    ILogger<StoredFileRemovalJob> logger)
{
    public const string RecurringJobId = "stored-file-removals";
    public static readonly TimeSpan AlertAfter = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    private const int BatchSize = 100;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RunAsync(CancellationToken ct)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        foreach (StoredFileRemoval removal in await removals.DueAsync(now, BatchSize, ct))
        {
            try
            {
                await RemoveAsync(removal, ct);
                await removals.CompleteAsync(removal.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Any failure is this entry's alone: it is retried, and the batch goes on.
                int attempts = removal.Attempts + 1;
                TimeSpan wait = WaitAfter(attempts);
                if (now - removal.CreatedAt >= AlertAfter)
                    logger.LogError(ex,
                        "The stored file {Path} in container {ContainerId} has not been removed after {Attempts} attempts over a day; still retrying",
                        LogSanitizer.Sanitize(removal.Path), removal.ContainerId, attempts);
                else
                    logger.LogWarning(ex, "Could not remove the stored file {Path} in container {ContainerId}; retrying in {Wait}",
                        LogSanitizer.Sanitize(removal.Path), removal.ContainerId, wait);

                await removals.RetryLaterAsync(removal.Id, now + wait, ex.Message, ct);
            }
        }
    }

    private async Task RemoveAsync(StoredFileRemoval removal, CancellationToken ct)
    {
        IConnector connector = storage.CreateConnector(removal.ContainerId.ToString());
        try
        {
            // Managed storage is always writable. A connector that isn't can't remove the file, so the entry stays
            // owed rather than being dropped with the file still there.
            if (connector is not IWritableConnector writable)
                throw new InvalidOperationException(
                    $"Storage for container {removal.ContainerId} can't remove files ({connector.GetType().Name}).");

            await documents.RemoveUnrecordedFileAsync(removal.ContainerId, removal.Path,
                token => writable.DeleteFileAsync(removal.Path, token), ct);
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    /// <summary>2, 4, 8 … minutes after each failed attempt, at most an hour.</summary>
    public static TimeSpan WaitAfter(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(attempts, 10)), MaxWait.TotalMinutes));
}
