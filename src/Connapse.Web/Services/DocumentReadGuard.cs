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
}
