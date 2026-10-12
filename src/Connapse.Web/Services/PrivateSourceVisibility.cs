using System.Security.Claims;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Web.Services;

/// <summary>
/// Whether a caller may see that a private GitHub source exists. Its name, description and counts
/// name a private repository, so outside search they are shown only to administrators (who manage
/// sources) and to people the GitHub permission check lets read the repository — the same answer
/// search gives for its documents. Every other source is visible as before.
/// </summary>
public sealed class PrivateSourceVisibility(
    ISearchScopeResolver resolver, IAuthorizationService authorization, IConnectionStore connections)
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

        HashSet<Guid>? atlassianConnections;
        try
        {
            atlassianConnections = (await connections.ListAsync(take: int.MaxValue, ct: ct))
                .Where(c => c.Provider == ConnectionProvider.Atlassian)
                .Select(c => c.Id)
                .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown: every connection-bound source stays hidden rather than risk listing one.
            atlassianConnections = null;
        }

        return source => IsVisible(source, granted, atlassianConnections);
    }

    /// <param name="atlassianConnections">
    /// The ids of every Atlassian connection, or null when they could not be read — in which case
    /// any source bound to a connection is hidden.
    /// </param>
    internal static bool IsVisible(
        Source source, IReadOnlySet<string> grantedPrefixes, IReadOnlySet<Guid>? atlassianConnections)
    {
        // A Confluence space's name can itself be sensitive, and no per-user answer is cheap enough
        // to ask per listing, so these are shown to administrators only (who return before here).
        if (IsConfluenceSpace(source.ScopeJson))
            return false;

        // The same holds for every kind an Atlassian connection carries, Jira's included, so the
        // connection decides rather than the kind.
        if (source.Provider == ConnectionProvider.Atlassian)
            return false;
        if (source.ConnectionId is { } connectionId
            && (atlassianConnections is null || atlassianConnections.Contains(connectionId)))
            return false;

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

    /// <summary>
    /// Whether a source hidden from this caller's listings may still be searched when the caller
    /// names it by id. A Confluence space is hidden for its name and description, not its pages:
    /// search checks every page against Confluence per hit and per user, so naming the space
    /// returns only what Confluence lets the caller read. A hidden private GitHub source stays
    /// unsearchable; being hidden is its permission answer.
    /// </summary>
    public static bool IsSearchableWhenHidden(Source source) => IsConfluenceSpace(source.ScopeJson);

    private static bool IsConfluenceSpace(string? scopeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(scopeJson ?? "{}");
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && string.Equals(kind.GetString(), "confluence-space", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
