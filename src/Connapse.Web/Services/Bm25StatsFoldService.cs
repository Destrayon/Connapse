using System.Diagnostics;
using Connapse.Storage.Keyword;

namespace Connapse.Web.Services;

/// <summary>
/// BM25 upkeep (#548): backfills markers and lengths on chunks from before the migration, then folds
/// the statistics deltas so readers sum few unfolded rows. Correctness never depends on the fold:
/// readers add unfolded deltas themselves.
/// </summary>
public class Bm25StatsFoldService(
    IServiceScopeFactory scopeFactory,
    ILogger<Bm25StatsFoldService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Backfill work per tick, so a large backlog never delays the fold for long.</summary>
    private static readonly TimeSpan BackfillBudget = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();

                var backfiller = scope.ServiceProvider.GetRequiredService<Bm25Backfiller>();
                var budget = Stopwatch.StartNew();
                int filled;
                do filled = await backfiller.BackfillBatchAsync(stoppingToken);
                while (filled > 0 && budget.Elapsed < BackfillBudget);

                await scope.ServiceProvider.GetRequiredService<Bm25StatsFolder>().FoldAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "BM25 backfill or statistics fold failed; retrying in {Interval}", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
