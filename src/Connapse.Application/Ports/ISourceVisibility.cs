using System.Security.Claims;
using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// Which sources a caller may know exist. A private GitHub source names a private repository and an
/// Atlassian source's name can itself be sensitive, so listings and reads hide them from anyone the
/// provider wouldn't show them to. Web answers it with the provider-specific checks it holds.
/// </summary>
public interface ISourceVisibility
{
    /// <summary>A filter for <paramref name="caller"/>, resolved once and reused across a listing.</summary>
    Task<Func<Source, bool>> ForAsync(ClaimsPrincipal? caller, CancellationToken ct = default);

    /// <summary>
    /// Whether a source hidden from a caller may still be searched when they name it by id: one whose
    /// every hit search checks against its provider for that caller, such as a Confluence space.
    /// </summary>
    bool IsSearchableWhenHidden(Source source);
}
