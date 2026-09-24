using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Which private GitHub repositories a user may search: every private GitHub source whose
/// repository GitHub says the user's linked account can read, as <c>github://{repoId}/</c> prefixes.
/// </summary>
/// <remarks>
/// Fails closed, row by row of the design's decision table: no principal, no linked account, no
/// private sources, an installation that cannot be reached, a 404, <c>none</c>, or any answer this
/// cannot interpret all contribute nothing — so the repository's documents, which carry a
/// <c>github://</c> address, stay hidden. Only read or higher opens a repository. Public GitHub
/// documents carry no address and are never filtered here.
/// </remarks>
public sealed class GitHubSearchScopeResolver(
    ISourceStore sources,
    IConnectionStore connections,
    IGitHubIdentityLinkReader links,
    GitHubRepositoryAccess access,
    ILogger<GitHubSearchScopeResolver> logger) : ISearchScopeResolver
{
    public const string Scheme = "github://";

    public static string DocumentPrefix(long repoId) => $"{Scheme}{repoId}/";

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is null) return SearchScopes.NoPrincipal;

        var link = await links.GetLinkAsync(userId.Value, ct);
        if (link is null) return SearchScopes.None;

        var repositories = await PrivateRepositoriesAsync(ct);
        if (repositories.Count == 0) return SearchScopes.None;

        var prefixes = new List<string>();
        foreach (var repo in repositories)
        {
            if (await access.CanReadAsync(repo, link, ct))
                prefixes.Add(DocumentPrefix(repo.RepoId));
        }

        return SearchScopes.OfPrefixes(prefixes);
    }

    /// <summary>Each private GitHub source's repository, with the installation its connection names.</summary>
    private async Task<IReadOnlyList<GitHubPrivateRepository>> PrivateRepositoriesAsync(CancellationToken ct)
    {
        var byConnection = (await connections.ListAsync(take: int.MaxValue, ct: ct))
            .Where(c => c.Provider == ConnectionProvider.GitHub)
            .ToDictionary(c => c.Id);

        var result = new Dictionary<long, GitHubPrivateRepository>();
        foreach (var source in await sources.ListAsync(take: int.MaxValue, ct: ct))
        {
            if (source.ConnectionId is not Guid connectionId || !byConnection.TryGetValue(connectionId, out var connection))
                continue;

            if (Parse(source.ScopeJson, connection.ConfigJson) is { } repo)
                result.TryAdd(repo.RepoId, repo);
            else if (IsPrivate(source.ScopeJson))
                logger.LogWarning("Private GitHub source {SourceId} is missing its repository id or installation; its documents stay hidden", source.Id);
        }

        return [.. result.Values];
    }

    internal static bool IsPrivate(string? scopeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(scopeJson ?? "{}");
            return doc.RootElement.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static GitHubPrivateRepository? Parse(string? scopeJson, string? connectionConfigJson)
    {
        try
        {
            using var scope = JsonDocument.Parse(scopeJson ?? "{}");
            using var config = JsonDocument.Parse(connectionConfigJson ?? "{}");
            var s = scope.RootElement; var c = config.RootElement;
            if (!(s.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True)) return null;
            if (!s.TryGetProperty("repoId", out var id) || !id.TryGetInt64(out long repoId) || repoId <= 0) return null;
            if (!c.TryGetProperty("installationId", out var inst) || !inst.TryGetInt64(out long installationId)) return null;
            string? owner = s.TryGetProperty("owner", out var o) ? o.GetString() : null;
            string? repo = s.TryGetProperty("repo", out var r) ? r.GetString() : null;
            return string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)
                ? null
                : new GitHubPrivateRepository(repoId, owner, repo, installationId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A private repository a source indexes, and the installation that may read it.</summary>
public sealed record GitHubPrivateRepository(long RepoId, string Owner, string Repo, long InstallationId);

/// <summary>
/// Asks GitHub, as the repository's own installation, whether a linked account may read it. Answers
/// are cached briefly per (installation, repository, account) — a definite answer for five minutes,
/// a failure for thirty seconds so an outage is not hammered — and every failure is a denial.
/// </summary>
public sealed class GitHubRepositoryAccess(
    GitHubCredentialPool pool,
    IHttpClientFactory httpClients,
    IMemoryCache cache,
    ILogger<GitHubRepositoryAccess> logger)
{
    private static readonly TimeSpan AnswerLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromHours(1);

    // GitHub's legacy "permission" is admin/write/read/none; "role_name" adds maintain and triage.
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
        { "admin", "maintain", "write", "triage", "read" };

    /// <summary>The REST API every call goes to. Only tests change it.</summary>
    internal string ApiBaseUrl { get; init; } = "https://api.github.com";

    public async Task<bool> CanReadAsync(GitHubPrivateRepository repo, GitHubIdentityRef account, CancellationToken ct)
    {
        string key = $"github-access:{repo.InstallationId}:{repo.RepoId}:{account.GitHubUserId}";
        if (cache.TryGetValue(key, out bool cached)) return cached;

        bool allowed;
        TimeSpan lifetime = AnswerLifetime;
        try
        {
            var api = new GitHubApiClient(httpClients.CreateClient(ConnectorFactory.GitHubHttpClientName), ApiBaseUrl,
                new GitHubAuth(pool, GitHubAccess.Pinned(repo.InstallationId)));
            string login = await CurrentLoginAsync(api, account, ct);
            var answer = await api.GetAsync<PermissionPayload>(
                $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Repo)}/collaborators/{Uri.EscapeDataString(login)}/permission", ct);
            allowed = (answer.Permission is { } p && Readable.Contains(p))
                      || (answer.RoleName is { } r && Readable.Contains(r));
        }
        catch (GitHubNotFoundException)
        {
            // The account, or the repository as this installation sees it, is not there: a denial,
            // and a definite one.
            allowed = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not check access to GitHub repository {RepoId}; its documents stay hidden for this search", repo.RepoId);
            allowed = false;
            lifetime = FailureLifetime;
        }

        cache.Set(key, allowed, lifetime);
        return allowed;
    }

    /// <summary>
    /// The account's login now: stored at link time, but a rename changes it and the permission
    /// endpoint takes a login. The numeric id is the key, so it is asked by id.
    /// </summary>
    private async Task<string> CurrentLoginAsync(GitHubApiClient api, GitHubIdentityRef account, CancellationToken ct)
    {
        string key = $"github-login:{account.GitHubUserId}";
        if (cache.TryGetValue(key, out string? login) && login is not null) return login;

        var user = await api.GetAsync<UserPayload>($"user/{account.GitHubUserId}", ct);
        login = string.IsNullOrWhiteSpace(user.Login) ? account.Login : user.Login;
        cache.Set(key, login, LoginLifetime);
        return login;
    }

    private sealed record PermissionPayload(string? Permission, string? RoleName);

    private sealed record UserPayload(string? Login);
}
