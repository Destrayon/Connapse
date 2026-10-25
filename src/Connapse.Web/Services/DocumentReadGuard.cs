using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Web.Services;

/// <summary>
/// Whether a caller may read one document directly, by id or path. Runs the same per-hit verifier
/// search runs, as if the document were the only hit, so a document search would hide is never
/// handed out by a direct read. Callers answer a denial exactly as they answer a missing document,
/// so a probe cannot learn that the document exists.
/// <para>
/// A document with no resource URI was uploaded rather than synced from a provider, so no verifier
/// governs it and none is run. Any failure while checking denies rather than throwing: the caller
/// then answers "not found", never a 500 that would itself say the document is there.
/// </para>
/// </summary>
public sealed class DocumentReadGuard(
    ISearchResultVerifier verifier,
    IDocumentStore documents,
    ILogger<DocumentReadGuard> logger)
{
    public async Task<bool> CanReadAsync(Guid? userId, Document document, CancellationToken ct)
    {
        try
        {
            IReadOnlyDictionary<string, string?> uris = await documents.GetResourceUrisAsync([document.Id], ct);
            if (!uris.TryGetValue(document.Id, out string? uri))
                return false; // the row went away under us
            if (uri is null)
                return true;

            return (await verifier.VerifyAsync([new SearchHit("", document.Id, "", 1f, [])], userId, 1, ct)).Count == 1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check whether a document may be read; denying");
            return false;
        }
    }

    /// <summary>
    /// The ids of those of <paramref name="candidates"/> the caller may read, with one verifier call for all of them, as
    /// a listing needs. Uploaded documents need no check; if checking fails, every synced one is denied.
    /// </summary>
    public async Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyList<Document> candidates, CancellationToken ct)
    {
        var readable = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count == 0)
            return readable;
        try
        {
            IReadOnlyDictionary<string, string?> uris = await documents.GetResourceUrisAsync([.. candidates.Select(d => d.Id)], ct);
            var synced = new List<SearchHit>();
            foreach (var document in candidates)
            {
                if (!uris.TryGetValue(document.Id, out string? uri))
                    continue; // the row went away under us
                if (uri is null)
                    readable.Add(document.Id);
                else
                    synced.Add(new SearchHit("", document.Id, "", 1f, []));
            }
            if (synced.Count == 0)
                return readable;

            try
            {
                foreach (var hit in await verifier.VerifyAsync(synced, userId, synced.Count, ct))
                    readable.Add(hit.DocumentId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not check whether synced documents may be read; denying them");
            }
            return readable;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check whether documents may be read; denying");
            return new HashSet<string>();
        }
    }
}
