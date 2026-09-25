using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Web.Services;

/// <summary>
/// How much depends on the GitHub App: its connections and their sources. Read-only, so the
/// Providers page can say what replacing or removing the App would stop without handling
/// connections itself; those are managed on the Connections page only.
/// </summary>
public sealed class GitHubAppUsage(IConnectionStore connections, ISourceStore sources)
{
    public async Task<(int Connections, int Sources)> CountAsync(CancellationToken ct = default)
    {
        var ids = (await connections.ListAsync(take: int.MaxValue, ct: ct))
            .Where(c => c.Provider == ConnectionProvider.GitHub)
            .Select(c => c.Id)
            .ToHashSet();
        int sourceCount = (await sources.ListAsync(take: int.MaxValue, ct: ct))
            .Count(s => s.ConnectionId is Guid id && ids.Contains(id));
        return (ids.Count, sourceCount);
    }
}
