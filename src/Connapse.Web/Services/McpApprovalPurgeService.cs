using Connapse.Identity.Services;

namespace Connapse.Web.Services;

/// <summary>
/// Purges MCP approval requests on a schedule (CON-185): an expired request's hash and sealed arguments go
/// within minutes whether or not another MCP call arrives, and a secret an older instance wrote readable
/// mid-deploy is hidden. Approving and matching never depend on this; it only shortens how long data stays.
/// </summary>
public class McpApprovalPurgeService(
    IServiceScopeFactory scopeFactory,
    ILogger<McpApprovalPurgeService> logger) : BackgroundService
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
                await scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>().PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "MCP approval purge failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
