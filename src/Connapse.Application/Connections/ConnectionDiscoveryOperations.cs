using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Connections;

/// <summary>
/// How asking a provider what a connection can reach went. <see cref="Denied"/> is the identity being scoped, not
/// broken: the name can still be typed, so it is an answer with a note rather than a failure.
/// </summary>
public enum DiscoveryOutcome { Listed, NotConfigured, Denied, Failed }

/// <summary>A Confluence space or Jira project an Atlassian site's credential can read.</summary>
public sealed record DiscoveredScope(string Id, string Key, string Name, string Type);

/// <summary>An installation of the GitHub App, which a GitHub connection is.</summary>
public sealed record DiscoveredInstallation(long Id, string AccountLogin, string AccountType, string RepositorySelection, string HtmlUrl);

/// <summary>A custom provider a custom connection may sit on. Its token is never part of it.</summary>
public sealed record DiscoveredCustomProvider(Guid Id, string Name, string BaseUrl);

/// <summary>
/// What a connection can reach, as its provider answered. Only the lists the provider has are set; <see cref="Message"/>
/// is what the Connections page says beside them: why nothing was listed, or that nothing exists.
/// </summary>
public sealed record ConnectionDiscovery(DiscoveryOutcome Outcome, string? Message = null, string? TroubleshootingUrl = null)
{
    public IReadOnlyList<string>? Buckets { get; init; }

    public string? BucketRegion { get; init; }

    public IReadOnlyList<AzureStorageAccountInfo>? StorageAccounts { get; init; }

    public IReadOnlyList<string>? Containers { get; init; }

    public IReadOnlyList<DiscoveredInstallation>? Installations { get; init; }

    /// <summary>GitHub's page for installing the App somewhere new.</summary>
    public string? InstallUrl { get; init; }

    public IReadOnlyList<DiscoveredScope>? Spaces { get; init; }

    public IReadOnlyList<DiscoveredScope>? Projects { get; init; }

    public IReadOnlyList<DiscoveredCustomProvider>? CustomProviders { get; init; }
}

/// <summary>What to ask of a stored connection: personal Confluence spaces are left out unless asked for.</summary>
public sealed record ConnectionDiscoveryRequest(bool IncludePersonalSpaces = false);

/// <summary>
/// What the Connections page asks before a connection is saved: the buckets a role can list, or one bucket's region;
/// the storage accounts Connapse's Azure identity sees, or the containers in one of them; the GitHub App's
/// installations; or the custom providers a custom connection can sit on.
/// </summary>
public sealed record DraftDiscovery(
    ConnectionProvider? Provider, string? RoleArn = null, string? Bucket = null, string? StorageAccountName = null, string? BlobEndpoint = null);

/// <summary>
/// Lists what a connection can reach, so an administrator picks a bucket, container, installation, space or project
/// instead of typing it: S3 buckets through the connection's role, the Azure identity's storage accounts and the
/// containers in the connection's account, the GitHub App's installations, and an Atlassian site's spaces or projects
/// through its stored credential. Administrators only; reads, never writes. A stored credential is only used with the
/// stored site it was saved for.
/// </summary>
[Operation("connections.discover", Scope = "admin:sources", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class DiscoverConnection(IConnectionStore connections, IConnectionDiscovery discovery, IOperationAuthorizer authorizer)
{
    private const string Forbidden = "You don't have permission to discover what connections can reach.";

    public async Task<Attempt<ConnectionDiscovery, ConnectionStatus>> RunAsync(
        OperationContext context, ConnectionRef key, ConnectionDiscoveryRequest? request = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, Forbidden);

        var current = await ConnectionRules.FindAsync(connections, key, ct);
        if (current is null)
            return ConnectionRules.NotFound<ConnectionDiscovery>(key);

        return await discovery.DiscoverAsync(current, request ?? new ConnectionDiscoveryRequest(), ct);
    }

    /// <summary>Lists what an unsaved connection could reach, for the pickers on the New connection form.</summary>
    public async Task<Attempt<ConnectionDiscovery, ConnectionStatus>> RunDraftAsync(
        OperationContext context, DraftDiscovery draft, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Forbidden, Forbidden);
        if (draft.Provider is not ({ } provider and (ConnectionProvider.S3 or ConnectionProvider.AzureBlob
            or ConnectionProvider.GitHub or ConnectionProvider.Custom)))
            return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Invalid,
                "Only S3, Azure Blob, GitHub and custom connections list anything before they are saved.", "provider");

        return await discovery.DiscoverDraftAsync(draft with { Provider = provider }, ct);
    }
}
