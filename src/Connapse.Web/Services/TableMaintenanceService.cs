using Connapse.Storage.Tables;

namespace Connapse.Web.Services;

/// <summary>
/// Drops tables that a reload replaced or a delete removed (CON-301), once the grace period for readers that already
/// resolved their names has passed. Nothing reads a retired table through the catalog, so a late drop only costs disk.
/// </summary>
public class TableMaintenanceService(
    IServiceScopeFactory scopeFactory,
    ILogger<TableMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PostgresTableStore>()
                    .DropRetiredTablesAsync(PostgresTableStore.RetiredTableGrace, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retired table cleanup failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
