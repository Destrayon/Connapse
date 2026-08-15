using Connapse.Storage.Backfill;

namespace Connapse.Web.Services;

/// <summary>
/// Runs the container-to-source backfill once at startup, after EF migrations have
/// applied. Idempotent: a second run finds nothing to migrate and returns immediately,
/// so restarts and multiple replicas are safe.
/// </summary>
public class SourceBackfillHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SourceBackfillHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<SourceBackfillService>();
            var report = await backfill.RunAsync(ct);

            if (report.ContainersMigrated > 0)
            {
                logger.LogInformation(
                    "Container-to-source backfill migrated {Count} container(s), repointing {Docs} document(s)",
                    report.ContainersMigrated, report.DocumentsRepointed);
            }

            foreach (var failure in report.Failures)
                logger.LogError("Backfill failure: {Failure}", failure);
        }
        catch (Exception ex)
        {
            // Never block startup on the backfill: the compatibility read means an
            // un-migrated install still serves every request correctly, so a failure
            // here should degrade rather than take the application down.
            logger.LogError(ex, "Container-to-source backfill failed; the application will start anyway");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
