namespace Connapse.Storage.Connectors.Custom;

/// <summary>
/// Stops paging a middle man that repeats a page token or never stops returning one. Without it a broken
/// middle man would hold a sync, or the New source dialog, in an endless loop while memory grows.
/// </summary>
internal sealed class PageGuard(string what, int maxPages = PageGuard.DefaultMaxPages)
{
    public const int DefaultMaxPages = 10_000;

    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Records the token for the next page; throws when it repeats or the page limit is passed.</summary>
    public void Next(string token)
    {
        if (!_seen.Add(token))
            throw new CustomSourceException($"The source server repeated a page token while paging {what}; stopped instead of looping.");
        if (_seen.Count >= maxPages)
            throw new CustomSourceException($"The source server returned more than {maxPages} pages of {what}; stopped.");
    }
}
