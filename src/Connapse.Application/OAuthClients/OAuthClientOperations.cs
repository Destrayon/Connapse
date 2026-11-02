using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.OAuthClients;

/// <summary>Why reading or revoking OAuth clients didn't succeed.</summary>
public enum OAuthClientStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "oauth-client-not-found", "OAuth client not found")]
    NotFound,
}

/// <summary>
/// Lists the OAuth clients with access to Connapse (MCP clients that registered themselves, or that are
/// known by a metadata URL) and how many people granted each. Administrators only.
/// </summary>
[Operation("oauthClients.list", Scope = OAuthClientScope.Name, Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListOAuthClients(IOAuthClientRegistry clients, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<OAuthClientSummary>, OAuthClientStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.ClientAdmin))
            return Attempt<IReadOnlyList<OAuthClientSummary>, OAuthClientStatus>.Fail(OAuthClientStatus.Forbidden, "Only administrators can see OAuth clients.");
        return Attempt<IReadOnlyList<OAuthClientSummary>, OAuthClientStatus>.Succeed(await clients.ListAsync(ct));
    }
}

/// <summary>
/// Revokes an OAuth client: its registration goes, so it can't start a new sign-in; every refresh token
/// it holds is revoked and its pending authorization codes deleted. Access tokens it already has expire
/// on their own. A client known by its metadata URL can sign in again if a person approves it again.
/// </summary>
[Operation("oauthClients.revoke", Scope = OAuthClientScope.Name, Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class RevokeOAuthClient(IOAuthClientRegistry clients, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public const string RevokedEvent = "oauth.client.revoked";

    public async Task<Attempt<OAuthClientRevocation, OAuthClientStatus>> RunAsync(OperationContext context, string clientId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.ClientAdmin))
            return Attempt<OAuthClientRevocation, OAuthClientStatus>.Fail(OAuthClientStatus.Forbidden, "Only administrators can revoke OAuth clients.");

        if (await clients.RevokeAsync(clientId, ct) is not { } revoked)
            return Attempt<OAuthClientRevocation, OAuthClientStatus>.Fail(OAuthClientStatus.NotFound, "No OAuth client with that id has access.");

        await audit.LogAsync(RevokedEvent, "oauth_client", clientId,
            new { revoked.ClientRemoved, revoked.RefreshTokensRevoked, revoked.AuthCodesRemoved }, ct);
        return Attempt<OAuthClientRevocation, OAuthClientStatus>.Succeed(revoked);
    }
}

/// <summary>The token scope both OAuth-client operations need.</summary>
public static class OAuthClientScope
{
    public const string Name = "admin:oauth-clients";
}
