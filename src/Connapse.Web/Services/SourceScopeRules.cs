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
