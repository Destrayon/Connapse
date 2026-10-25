using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Storage.Connectors.Custom;

namespace Connapse.Application.Links;

/// <summary>Why unlinking an account didn't succeed.</summary>
public enum AccountUnlinkStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-link-provider", "Unknown provider")]
    InvalidProvider,

    [Failure(404, "link-not-found", "No such link")]
    NotFound,

    [Failure(409, "link-changed", "The link changed")]
    Changed,

    [Failure(502, "link-provider-unavailable", "The provider didn't answer")]
    Unavailable,
}

/// <summary>
/// Which link to remove: <c>AWS</c>, <c>Azure</c>, <c>GitHub</c> or <c>Atlassian</c> (any case), or
/// <c>custom</c> with the custom provider's id.
/// </summary>
public sealed record UnlinkAccountRequest(string Provider, Guid? CustomProviderId = null);

/// <summary>
/// Removes one of the caller's own account links, so search stops showing what only that account may
/// read. A person signing in only: a personal access token or agent key, which an agent holds, may read
/// links but not remove them. A person's own bearer token (CLI or OAuth sign-in) may, as before.
/// </summary>
[Operation("links.unlink", Scope = "knowledge:write", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class UnlinkAccount(
    IAwsIdentityLinkService aws,
    IAzureIdentityLinkService azure,
    GitHubIdentityLinkStore gitHub,
    GitHubLinkFlow gitHubFlow,
    AtlassianIdentityLinkStore atlassian,
    AtlassianLinkFlow atlassianFlow,
    ICustomAccountLinks custom,
    IConnectorScopeCache scopeCache,
    ISourceStore sources,
    IConnectionStore connections,
    IAuditLogger audit,
    IOperationAuthorizer authorizer)
{
    public const string GitHubUnlinkedEvent = "identity.github.unlinked";
    public const string AtlassianUnlinkedEvent = "identity.atlassian.unlinked";
    public const string CustomUnlinkedEvent = "identity.custom.unlinked";

    public async Task<Attempt<UnlinkAccountRequest, AccountUnlinkStatus>> RunAsync(
        OperationContext context, UnlinkAccountRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Person)
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid userId))
            return Fail(AccountUnlinkStatus.Forbidden, "Only you can unlink your accounts, signed in yourself; a personal access token or agent key can't.");

        if (string.Equals(request.Provider, "custom", StringComparison.OrdinalIgnoreCase) && request.CustomProviderId is Guid providerId)
            return await UnlinkCustomAsync(request, providerId, userId, ct);

        if (!Enum.TryParse<CloudProvider>(request.Provider, ignoreCase: true, out var provider)
            || provider is not (CloudProvider.AWS or CloudProvider.Azure or CloudProvider.GitHub or CloudProvider.Atlassian))
            return Fail(AccountUnlinkStatus.InvalidProvider, $"Unknown provider: {request.Provider}. Valid values: AWS, Azure, GitHub, Atlassian.");

        switch (provider)
        {
            case CloudProvider.Atlassian:
                // Refused first, so a sign-in racing the delete cannot put the link back.
                atlassianFlow.RevokeFor(userId);
                if (!await atlassian.DeleteAsync(userId, ct))
                    return Fail(AccountUnlinkStatus.NotFound, "No Atlassian account link found.");
                await audit.LogAsync(AtlassianUnlinkedEvent, "user", userId.ToString(), null, ct);
                break;

            case CloudProvider.GitHub:
                gitHubFlow.RevokeFor(userId);
                if (!await gitHub.DeleteAsync(userId, ct))
                    return Fail(AccountUnlinkStatus.NotFound, "No GitHub account link found.");
                await audit.LogAsync(GitHubUnlinkedEvent, "user", userId.ToString(), null, ct);
                break;

            case CloudProvider.Azure:
                // Azure permissions are resolved live per search hit rather than cached against
                // the link, so removing the link is the whole revocation.
                if (!await azure.DisconnectAsync(userId, ct))
                    return Fail(AccountUnlinkStatus.NotFound, "No Entra identity link found.");
                break;

            default:
                var result = await aws.DisconnectAsync(userId, ct);
                if (result.LinkChangedDuringDisconnect)
                    return Fail(AccountUnlinkStatus.Changed, "Your AWS identity changed while disconnecting. Please try again.");
                if (!result.Deleted)
                    return Fail(AccountUnlinkStatus.NotFound, "No AWS identity link found.");
                await EvictAwsScopesAsync(userId, ct);
                break;
        }
        return Attempt<UnlinkAccountRequest, AccountUnlinkStatus>.Succeed(request);
    }

    private async Task<Attempt<UnlinkAccountRequest, AccountUnlinkStatus>> UnlinkCustomAsync(
        UnlinkAccountRequest request, Guid providerId, Guid userId, CancellationToken ct)
    {
        try
        {
            await custom.UnlinkAsync(providerId, userId, ct);
        }
        catch (CustomProviderNotFoundException)
        {
            return Fail(AccountUnlinkStatus.NotFound, "No custom provider has that id.");
        }
        catch (Exception ex) when (ex is CustomSourceException or HttpRequestException)
        {
            return Fail(AccountUnlinkStatus.Unavailable, "The custom provider's source server did not remove the link, so you are still linked. Try again later.");
        }
        await audit.LogAsync(CustomUnlinkedEvent, "user", userId.ToString(), new { ProviderId = providerId }, ct);
        return Attempt<UnlinkAccountRequest, AccountUnlinkStatus>.Succeed(request);
    }

    /// <summary>
    /// Forgets what the user's AWS link allowed on each S3 source. Sources, not containers (#353): the
    /// scope cache is keyed on the thing whose access was cached. Best effort; entries expire anyway.
    /// </summary>
    private async Task EvictAwsScopesAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var s3 = (await connections.ListAsync(take: int.MaxValue, ct: ct))
                .Where(c => c.Provider == ConnectionProvider.S3).Select(c => c.Id).ToHashSet();
            if (s3.Count == 0)
                return;
            foreach (var source in (await sources.ListAsync(take: int.MaxValue, ct: ct)).Where(s => s.ConnectionId is Guid id && s3.Contains(id)))
                scopeCache.Invalidate(userId, source.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort eviction: the cache expires on its own.
        }
    }

    private static Attempt<UnlinkAccountRequest, AccountUnlinkStatus> Fail(AccountUnlinkStatus status, string detail) =>
        Attempt<UnlinkAccountRequest, AccountUnlinkStatus>.Fail(status, detail);
}
