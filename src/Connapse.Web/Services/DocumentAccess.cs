using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;

namespace Connapse.Web.Services;

/// <summary>Answers the file operations' read check with the read guard, for the person the caller acts for.</summary>
public sealed class DocumentAccess(DocumentReadGuard guard) : IDocumentAccess
{
    public Task<bool> CanReadAsync(ClaimsPrincipal user, Document document, CancellationToken ct) =>
        guard.CanReadAsync(SearchPrincipal.Resolve(user), document, ct);
}
