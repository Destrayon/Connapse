using Connapse.Application.Search;
using Connapse.Core;

namespace Connapse.Application.Memory;

/// <summary>The passages in a container's documents that read close to a memory about to be saved there.</summary>
public interface IDocumentPassages
{
    /// <summary>The close passages to <paramref name="text"/>, closest first; or why the container could not be searched.</summary>
    Task<PassageLookup> FindAsync(OperationContext context, Guid containerId, string text, CancellationToken ct = default);
}

/// <summary>The passages found, or the reason none could be looked for.</summary>
public sealed record PassageLookup(IReadOnlyList<MemoryPassage> Passages, string? Error = null)
{
    public static PassageLookup Failed(string error) => new([], error);
}

/// <summary>
/// Finds close passages with the container's Semantic search: a passage is close when its cosine similarity to the
/// memory is at least <see cref="CloseCosine"/>. Without embeddings there is no cosine, so the lookup fails.
/// </summary>
public sealed class SearchDocumentPassages(SearchContainer search) : IDocumentPassages
{
    /// <summary>
    /// The cosine at which a passage counts as close: the cutoff tuned on the dev split of the CON-245 sorting eval
    /// (eval/agent-search/con-245), the highest that kept every new fact there. It was tuned on nomic-embed-text.
    /// </summary>
    public const float CloseCosine = 0.8893f;

    public async Task<PassageLookup> FindAsync(OperationContext context, Guid containerId, string text, CancellationToken ct = default)
    {
        Attempt<SearchResult, SearchStatus> found = await search.RunAsync(context, containerId,
            new SearchRequest(text, SearchMode.Semantic, RememberFact.PassagesShown, CloseCosine), ct);
        return found.Succeeded
            ? new PassageLookup([.. found.Result!.Hits.Select(Passage)])
            : PassageLookup.Failed(found.Detail ?? found.Status.ToString());
    }

    private static MemoryPassage Passage(SearchHit hit) => new(
        Guid.TryParse(hit.DocumentId, out Guid id) ? id : Guid.Empty,
        hit.Metadata.GetValueOrDefault("fileName") ?? hit.DocumentId,
        hit.Content);
}
