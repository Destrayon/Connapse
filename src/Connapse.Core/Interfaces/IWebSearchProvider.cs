namespace Connapse.Core.Interfaces;

public interface IWebSearchProvider
{
    Task<WebSearchResult> SearchAsync(string query, WebSearchOptions? options = null, CancellationToken ct = default);
}
