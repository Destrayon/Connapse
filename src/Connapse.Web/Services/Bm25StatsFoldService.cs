using Connapse.Storage.Keyword;

namespace Connapse.Web.Services;

/// <summary>
/// Folds the BM25 statistics deltas every few seconds so readers sum few unfolded rows (#548).
/// Correctness never depends on it: readers add unfolded deltas themselves.
/// </summary>
public class Bm25StatsFoldService(
    IServiceScopeFactory scopeFactory,
    ILogger<Bm25StatsFoldService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Bm25StatsFolder>().FoldAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Folding BM25 statistics failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
