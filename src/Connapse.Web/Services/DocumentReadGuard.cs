using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Web.Services;

/// <summary>
/// Whether a caller may read one document directly, by id or path. Runs the same per-hit verifier
/// search runs, as if the document were the only hit, so a document search would hide is never
/// handed out by a direct read. Callers answer a denial exactly as they answer a missing document,
/// so a probe cannot learn that the document exists.
/// </summary>
public sealed class DocumentReadGuard(ISearchResultVerifier verifier)
{
    public async Task<bool> CanReadAsync(Guid? userId, string documentId, CancellationToken ct) =>
        (await verifier.VerifyAsync([new SearchHit("", documentId, "", 1f, [])], userId, 1, ct)).Count == 1;
}
