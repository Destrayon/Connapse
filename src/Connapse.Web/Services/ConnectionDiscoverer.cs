using Connapse.Application;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Settings;

namespace Connapse.Web.Services;

/// <summary>
/// The Connections page's pickers, which connections.discover calls: S3 buckets through a connection's role, Azure
/// storage accounts and containers through Connapse's identity, the GitHub App's installations, an Atlassian site's
/// spaces or projects through its stored credential, and the custom providers a custom connection can sit on.
/// </summary>
public sealed class ConnectionDiscoverer(
    IS3Discovery s3,
    IAzureBlobDiscovery azure,
    IGitHubAppInstallations gitHubApp,
    IProviderCredentialStore credentials,
    ICustomProviderStore customProviders,
    AtlassianSiteService atlassianSites) : IConnectionDiscovery
{
    private const string NoAzureIdentity = "Connapse has no Azure identity yet — set one up on the Azure provider page.";

    public async Task<Attempt<ConnectionDiscovery, ConnectionStatus>> DiscoverAsync(
        Connection connection, ConnectionDiscoveryRequest request, CancellationToken ct = default)
    {
        var form = ConnectionForm.FromConnection(connection);
        switch (connection.Provider)
        {
            case ConnectionProvider.S3:
                return Ok(await BucketsAsync(form.RoleArn, ct));

            case ConnectionProvider.AzureBlob:
                var accounts = await StorageAccountsAsync(ct);
                // Only the connection's own account is listed: an endpoint for some other host gets nothing sent to it.
                if (AzureBlobEndpoint.Validate(form.StorageAccountName, form.BlobEndpoint, out Uri? endpoint) is not null)
                    return Ok(accounts);
                var containers = await ContainersAsync(endpoint!, ct);
                return Ok(containers with { StorageAccounts = accounts.StorageAccounts });

            case ConnectionProvider.GitHub:
                return Ok(await InstallationsAsync(ct));

            case ConnectionProvider.Atlassian:
                return await AtlassianAsync(connection, request, ct);

            default:
                return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Invalid,
                    $"A {connection.Provider} connection has nothing to list.", "provider");
        }
    }

    public async Task<Attempt<ConnectionDiscovery, ConnectionStatus>> DiscoverDraftAsync(DraftDiscovery draft, CancellationToken ct = default)
    {
        switch (draft.Provider)
        {
            case ConnectionProvider.S3 when !string.IsNullOrWhiteSpace(draft.Bucket):
                return Ok(await RegionAsync(draft.Bucket.Trim(), draft.RoleArn, ct));

            case ConnectionProvider.S3:
                return Ok(await BucketsAsync(draft.RoleArn, ct));

            case ConnectionProvider.AzureBlob when string.IsNullOrWhiteSpace(draft.StorageAccountName):
                return Ok(await StorageAccountsAsync(ct));

            case ConnectionProvider.AzureBlob:
                // An override is listed only when it is shown to be the named account's own, so Connapse's token
                // is never sent to a host of the caller's choosing.
                if (AzureBlobEndpoint.Validate(draft.StorageAccountName, draft.BlobEndpoint, out Uri? endpoint) is { } problem)
                    return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Invalid, problem, "blobEndpoint");
                return Ok(await ContainersAsync(endpoint!, ct));

            case ConnectionProvider.GitHub:
                return Ok(await InstallationsAsync(ct));

            case ConnectionProvider.Custom:
                var registered = await customProviders.ListAsync(ct);
                return Ok(new ConnectionDiscovery(DiscoveryOutcome.Listed)
                {
                    CustomProviders = [.. registered.Select(p => new DiscoveredCustomProvider(p.Id, p.Name, p.BaseUrl))],
                });

            default:
                return Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.Invalid,
                    $"A new {draft.Provider} connection has nothing to list.", "provider");
        }
    }

    /// <remarks>
    /// Through the connection's role rather than the ambient credential, so a connection that assumes a role into
    /// another account lists that account's buckets. The call needs <c>s3:ListAllMyBuckets</c>, which narrow
    /// per-bucket policies omit, so a denial is a note rather than an error.
    /// </remarks>
    private async Task<ConnectionDiscovery> BucketsAsync(string? roleArn, CancellationToken ct)
    {
        var probe = await s3.ListBucketsAsync(NullIfBlank(roleArn), ct);
        return probe.Outcome switch
        {
            AwsProbeOutcome.Succeeded => new ConnectionDiscovery(DiscoveryOutcome.Listed,
                probe.Value is { Count: 0 } ? "No buckets in this account." : null)
            { Buckets = probe.Value ?? [] },
            AwsProbeOutcome.NoCredentials => new ConnectionDiscovery(DiscoveryOutcome.NotConfigured,
                "Connapse has no AWS identity yet — set one up on the AWS provider page."),
            AwsProbeOutcome.Denied => new ConnectionDiscovery(DiscoveryOutcome.Denied,
                "This identity may not list buckets, so type the name instead."),
            _ => new ConnectionDiscovery(DiscoveryOutcome.Failed, probe.Detail),
        };
    }

    /// <summary>Where AWS says a bucket lives. A region nobody could look up is one somebody can type.</summary>
    private async Task<ConnectionDiscovery> RegionAsync(string bucket, string? roleArn, CancellationToken ct)
    {
        try
        {
            var probe = await s3.GetBucketRegionAsync(bucket, NullIfBlank(roleArn), ct);
            return probe is { Succeeded: true, Value: { Length: > 0 } region }
                ? new ConnectionDiscovery(DiscoveryOutcome.Listed) { BucketRegion = region }
                : new ConnectionDiscovery(probe.Outcome switch
                {
                    AwsProbeOutcome.NoCredentials => DiscoveryOutcome.NotConfigured,
                    AwsProbeOutcome.Denied => DiscoveryOutcome.Denied,
                    _ => DiscoveryOutcome.Failed,
                }, "Connapse could not work out which region this bucket is in. Enter one.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ConnectionDiscovery(DiscoveryOutcome.Failed, "Connapse could not work out which region this bucket is in. Enter one.");
        }
    }

    private async Task<ConnectionDiscovery> StorageAccountsAsync(CancellationToken ct)
    {
        var probe = await azure.ListStorageAccountsAsync(ct);
        return probe.Outcome switch
        {
            AzureProbeOutcome.Succeeded => new ConnectionDiscovery(DiscoveryOutcome.Listed,
                probe.Value is { Count: 0 } ? "No storage accounts in this subscription." : null)
            { StorageAccounts = probe.Value ?? [] },
            AzureProbeOutcome.NotConfigured => new ConnectionDiscovery(DiscoveryOutcome.NotConfigured, NoAzureIdentity),
            AzureProbeOutcome.Denied => new ConnectionDiscovery(DiscoveryOutcome.Denied,
                "This identity may not list storage accounts, so type the name instead."),
            _ => new ConnectionDiscovery(DiscoveryOutcome.Failed, probe.Detail),
        };
    }

    private async Task<ConnectionDiscovery> ContainersAsync(Uri endpoint, CancellationToken ct)
    {
        var probe = await azure.ListContainersAsync(endpoint.ToString().TrimEnd('/'), ct);
        return probe.Outcome switch
        {
            AzureProbeOutcome.Succeeded => new ConnectionDiscovery(DiscoveryOutcome.Listed,
                probe.Value is { Count: 0 } ? "No containers in this storage account." : null)
            { Containers = probe.Value ?? [] },
            AzureProbeOutcome.NotConfigured => new ConnectionDiscovery(DiscoveryOutcome.NotConfigured, NoAzureIdentity),
            AzureProbeOutcome.Denied => new ConnectionDiscovery(DiscoveryOutcome.Denied,
                "This identity may not list containers here, so type the name instead."),
            _ => new ConnectionDiscovery(DiscoveryOutcome.Failed, probe.Detail),
        };
    }

    /// <summary>GitHub connections are installations of the provider's App, so none can exist without it.</summary>
    private async Task<ConnectionDiscovery> InstallationsAsync(CancellationToken ct)
    {
        try
        {
            if (!await gitHubApp.IsConfiguredAsync(ct))
                return new ConnectionDiscovery(DiscoveryOutcome.NotConfigured, "No GitHub App is set up yet.");

            var installations = await gitHubApp.ListInstallationsAsync(ct);
            string? installUrl = await credentials.GetGitHubAppAsync(ct) is { } app ? app.HtmlUrl.TrimEnd('/') + "/installations/new" : null;
            return new ConnectionDiscovery(DiscoveryOutcome.Listed)
            {
                Installations = [.. installations.Select(i => new DiscoveredInstallation(i.Id, i.AccountLogin, i.AccountType, i.RepositorySelection, i.HtmlUrl))],
                InstallUrl = installUrl,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ConnectionDiscovery(DiscoveryOutcome.Failed, "Could not list the GitHub App's installations. " + GitHubMessages.Describe(ex),
                GitHubMessages.TroubleshootingUrl);
        }
    }

    /// <summary>A Confluence site's spaces or a Jira site's projects, each listed only with its own product's credential.</summary>
    private async Task<Attempt<ConnectionDiscovery, ConnectionStatus>> AtlassianAsync(
        Connection connection, ConnectionDiscoveryRequest request, CancellationToken ct)
    {
        AtlassianSpacesOutcome outcome;
        List<DiscoveredScope>? items;
        string? error, url;
        bool jira = AtlassianSite.FromConfigJson(connection.ConfigJson)?.Product == AtlassianProduct.Jira;
        if (jira)
        {
            var projects = await atlassianSites.ListProjectsAsync(connection.Id, ct);
            (outcome, error, url) = (projects.Outcome, projects.Error, projects.TroubleshootingUrl);
            items = projects.Projects?.Select(i => new DiscoveredScope(i.Id, i.Key, i.Name, i.ProjectTypeKey)).ToList();
        }
        else
        {
            var spaces = await atlassianSites.ListSpacesAsync(connection.Id, request.IncludePersonalSpaces, ct);
            (outcome, error, url) = (spaces.Outcome, spaces.Error, spaces.TroubleshootingUrl);
            items = spaces.Spaces?.Select(i => new DiscoveredScope(i.Id, i.Key, i.Name, i.Type)).ToList();
        }

        return outcome switch
        {
            AtlassianSpacesOutcome.Listed => Ok(jira
                ? new ConnectionDiscovery(DiscoveryOutcome.Listed) { Projects = items ?? [] }
                : new ConnectionDiscovery(DiscoveryOutcome.Listed) { Spaces = items ?? [] }),
            // Deleted, or no longer an Atlassian site, since it was read.
            AtlassianSpacesOutcome.NotFound or AtlassianSpacesOutcome.NotAtlassian =>
                Attempt<ConnectionDiscovery, ConnectionStatus>.Fail(ConnectionStatus.NotFound, $"Connection {connection.Id} not found"),
            _ => Ok(new ConnectionDiscovery(DiscoveryOutcome.Failed, error, url)),
        };
    }

    private static Attempt<ConnectionDiscovery, ConnectionStatus> Ok(ConnectionDiscovery discovery) =>
        Attempt<ConnectionDiscovery, ConnectionStatus>.Succeed(discovery);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
