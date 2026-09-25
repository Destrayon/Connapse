using System.Security.Claims;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Web.Services;

/// <summary>
/// Whether a caller may see that a private GitHub source exists. Its name, description and counts
/// name a private repository, so outside search they are shown only to administrators (who manage
/// sources) and to people the GitHub permission check lets read the repository — the same answer
/// search gives for its documents. Every other source is visible as before.
/// </summary>
public sealed class PrivateSourceVisibility(ISearchScopeResolver resolver, IAuthorizationService authorization)
{
    /// <summary>A filter for <paramref name="caller"/>, resolved once and reused across a listing.</summary>
    public async Task<Func<Source, bool>> ForAsync(ClaimsPrincipal? caller, CancellationToken ct = default)
    {
        if (caller is not null && (await authorization.AuthorizeAsync(caller, "RequireAdmin")).Succeeded)
            return _ => true;

        Guid? userId = SearchPrincipal.Resolve(caller);
        HashSet<string> granted;
        try
        {
            var scopes = ScopeResolution.Guard(await resolver.ResolveAsync(userId, ct), userId);
            granted = scopes.Matches
                .Where(m => m.Value.StartsWith(GitHubSearchScopeResolver.Scheme, StringComparison.Ordinal))
                .Select(m => m.Value)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unanswerable is not a permit: every private source stays hidden.
            granted = [];
        }

        return source => IsVisible(source, granted);
    }

    internal static bool IsVisible(Source source, IReadOnlySet<string> grantedPrefixes)
    {
        if (!GitHubSearchScopeResolver.IsPrivate(source.ScopeJson))
        {
            // A public GitHub source whose repository a sync found it can no longer read (most
            // often, made private) is treated as private from then on: its name and summary would
            // otherwise keep describing a repository its documents are already hidden for.
            return source.AccessRevokedAt is null || !GitHubSearchScopeResolver.IsGitHubScope(source.ScopeJson);
        }

        // A private source whose repository id cannot be read is hidden: there is nothing to check.
        return GitHubSearchScopeResolver.RepoIdOf(source.ScopeJson) is { } repoId
            && grantedPrefixes.Contains(GitHubSearchScopeResolver.DocumentPrefix(repoId));
    }
}
