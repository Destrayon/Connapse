using System.Security.Claims;
using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// Whether a caller may read one document directly, the check search runs on each hit. Web answers it with its
/// read guard, which Connapse.Application doesn't reference. A denial reads exactly as a missing document.
/// </summary>
public interface IDocumentAccess
{
    Task<bool> CanReadAsync(ClaimsPrincipal user, Document document, CancellationToken ct);

    /// <summary>The ids of those of <paramref name="documents"/> the caller may read, checked together.</summary>
    Task<IReadOnlySet<string>> ReadableAsync(ClaimsPrincipal user, IReadOnlyList<Document> documents, CancellationToken ct);
}
