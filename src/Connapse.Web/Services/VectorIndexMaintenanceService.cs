using Connapse.Storage.Vectors;

namespace Connapse.Web.Services;

/// <summary>
/// Keeps per-container vector indexes in line with the data (#571): a container that grows past
/// the index threshold gets its index a few minutes later, and indexes of shrunk or deleted
/// containers are dropped. Builds take minutes for large containers, so they run here rather than
/// at startup. Search is exact until an index exists, so correctness never depends on this.
/// </summary>
public class VectorIndexMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<VectorIndexMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<VectorColumnManager>().EnsureIndexesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Vector index maintenance failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
