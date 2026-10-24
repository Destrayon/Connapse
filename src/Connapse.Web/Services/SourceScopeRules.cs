using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;

namespace Connapse.Web.Services;

/// <summary>The provider rules for a source's scope, which the Sources operations call.</summary>
public sealed class SourceScopeRules(IConnectionStore connections) : ISourceScopeRules
{
    public async Task<ScopeCheck> CheckUpdateAsync(Source current, string requestedScopeJson, CancellationToken ct = default)
    {
        // A GitHub source's documents are addressed — or deliberately not — by whether it is
        // private, and keyed on its repository id. Changing either in place would leave the
        // documents already indexed with the old addressing until a re-sync replaced them:
        // public rows (no address) of a repository now marked private would stay visible to
        // everyone. So they are fixed at creation; to change them, add the repository again.
        if (!string.IsNullOrWhiteSpace(requestedScopeJson)
            && SourcesEndpoints.GitHubIdentityChanged(current.ScopeJson, requestedScopeJson))
        {
            return ScopeCheck.Refuse("A GitHub source's private flag and repository id cannot be changed. Add the repository again as a new source.");
        }

        var connection = current.ConnectionId is Guid connectionId ? await connections.GetAsync(connectionId, ct) : null;

        // A custom source's scope names the middle man its connection's token is sent to. Changing it in
        // place would let anyone who may edit sources point that token at another server, so it is fixed
        // at creation; to read elsewhere, create a new source.
        if ((current.Provider is ConnectionProvider.Custom || connection is { Provider: ConnectionProvider.Custom })
            && CustomSourceScope.TryParse(requestedScopeJson) != CustomSourceScope.TryParse(current.ScopeJson))
        {
            return ScopeCheck.Refuse("A custom source's source server and scope cannot be changed. Create a new source instead.", "scope");
        }

        // A Confluence source keeps its space; what it reads from it is checked as on creation.
        // A Jira source, known by its stored kind, keeps its project the same way.
        if (connection is { Provider: ConnectionProvider.Atlassian })
        {
            var atlassian = AtlassianSourceScope.IsJira(current.ScopeJson)
                ? JiraSourceScope.Update(current.ScopeJson, requestedScopeJson)
                : ConfluenceSourceScope.Update(current.ScopeJson, requestedScopeJson);
            return atlassian.Outcome == AtlassianSourceScope.Outcome.Valid
                ? new ScopeCheck(atlassian.ScopeJson)
                : ScopeCheck.Refuse(atlassian.Error!, atlassian.Field);
        }

        return new ScopeCheck(requestedScopeJson);
    }
}

/// <summary>The provider checks for a new source's scope, which the create operation calls.</summary>
public sealed class SourceCreateRules(
    ISourceStore sources,
    CustomSourceSetup customSources,
    Connapse.Storage.Connectors.GitHub.GitHubRepositoryLookup gitHubRepositories,
    AtlassianSiteService atlassianSites,
    SourceScopePreflight preflight) : ISourceCreateRules
{
    public async Task<ScopeCheck> CheckCreateAsync(Connection connection, string? scopeJson, CancellationToken ct = default)
    {
        switch (connection.Provider)
        {
            // The connection's source server answers with its token, speaks protocol version 1, and offers
            // the scope. The scope names only the scopeId; a baseUrl, if given, must be the connection's,
            // and the one stored is the connection's normalised address.
            case ConnectionProvider.Custom:
                {
                    (string? scopeId, string? baseUrl) = SourcesEndpoints.CustomScopeRequest(scopeJson);
                    if (scopeId is null)
                        return ScopeCheck.Refuse("The scope must name the source server's scopeId.", "scopeId");

                    CustomSourceProbe probe = await customSources.ProbeConnectionAsync(connection, ct);
                    if (probe.Error is not null)
                        return ScopeCheck.Refuse(probe.Error, probe.Field, probe.Unreachable ? ScopeRefusal.Unavailable : ScopeRefusal.Invalid);

                    if (baseUrl is not null && !string.Equals(baseUrl.TrimEnd('/'), probe.BaseUrl, StringComparison.OrdinalIgnoreCase))
                        return ScopeCheck.Refuse(
                            $"A custom source reads its connection's source server ({probe.BaseUrl}). Leave baseUrl out, or pass that one.", "baseUrl");

                    if (!probe.Scopes.Any(s => s.Id == scopeId))
                        return ScopeCheck.Refuse($"The source server offers no scope '{scopeId}'.", "scopeId");

                    return new ScopeCheck(new CustomSourceScope(probe.BaseUrl!, scopeId).ToJson());
                }

            // GitHub, as this installation, says which repository the name is and whether it is private.
            // Its answer replaces whatever the request claimed.
            case ConnectionProvider.GitHub:
                {
                    var resolved = await GitHubSourceScope.ResolveAsync(scopeJson, connection, gitHubRepositories, ct);
                    return resolved.Error is not null ? ScopeCheck.Refuse(resolved.Error) : new ScopeCheck(resolved.ScopeJson);
                }

            // A Confluence space or a Jira project (by the scope's kind; none means Confluence), checked
            // against the site as its service account; the scope stored is rebuilt from Atlassian's answer.
            case ConnectionProvider.Atlassian:
                {
                    var atlassian = AtlassianSourceScope.IsJira(scopeJson)
                        ? await JiraSourceScope.ResolveAsync(scopeJson, connection, atlassianSites, sources, ct)
                        : await ConfluenceSourceScope.ResolveAsync(scopeJson, connection, atlassianSites, sources, ct);
                    return atlassian.Outcome switch
                    {
                        AtlassianSourceScope.Outcome.Invalid => ScopeCheck.Refuse(atlassian.Error!, atlassian.Field),
                        AtlassianSourceScope.Outcome.Duplicate => ScopeCheck.Refuse(atlassian.Error!, atlassian.Field, ScopeRefusal.Duplicate),
                        AtlassianSourceScope.Outcome.Unavailable => ScopeCheck.Refuse(atlassian.Error!, atlassian.Field, ScopeRefusal.Unavailable),
                        _ => new ScopeCheck(atlassian.ScopeJson) { DefaultName = atlassian.DefaultName },
                    };
                }

            // A storage scope is checked against the connection's allowed locations or roots before it is
            // saved, so a refusal is legible now rather than a failed sync later; ConnectorFactory still
            // enforces on every sync.
            case ConnectionProvider.S3 or ConnectionProvider.AzureBlob or ConnectionProvider.Filesystem or ConnectionProvider.Sftp:
                {
                    // Scope JSON that doesn't parse is the create's own refusal, worded as it always was.
                    if (!ParsesAsJson(scopeJson))
                        return new ScopeCheck(scopeJson);
                    var checkedScope = preflight.Check(connection, string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson);
                    return checkedScope.IsRefused
                        ? ScopeCheck.Refuse(checkedScope.Error!, "scopeJson")
                        : new ScopeCheck(scopeJson) { Warning = checkedScope.Warning };
                }

            default:
                return new ScopeCheck(scopeJson);
        }
    }

    /// <summary>Whether a scope parses as JSON; blank does, since a blank scope is read as "{}".</summary>
    private static bool ParsesAsJson(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
            return true;
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(scopeJson);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
