using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Reports what each cloud provider still needs.
/// </summary>
/// <remarks>
/// Reads from the settings store and from AWS itself; writes nothing. The page it feeds is a view,
/// and keeping the reader write-free is what stops that page turning into a place credentials live.
/// </remarks>
public class ProviderSetupReader(
    IOptionsMonitor<AwsSsoSettings> awsSso,
    IOptionsMonitor<AzureAdSettings> azureAd,
    IS3Discovery s3Discovery,
    IConnectionStore connections,
    ILogger<ProviderSetupReader> logger) : IProviderSetupReader
{
    public async Task<IReadOnlyList<ProviderSetup>> ReadAsync(CancellationToken ct = default)
    {
        var providers = await InUseProvidersAsync(ct);

        return
        [
            new ProviderSetup("aws", "AWS",
                [SignIn(awsSso.CurrentValue), await Access(ct)],
                InUse: IsConfigured(awsSso.CurrentValue) || providers.Contains(ConnectionProvider.S3)),

            new ProviderSetup("azure", "Azure",
                [SignIn(azureAd.CurrentValue), AzureAccess()],
                InUse: IsConfigured(azureAd.CurrentValue) || providers.Contains(ConnectionProvider.AzureBlob))
        ];
    }

    /// <summary>
    /// Which cloud providers this installation has actually taken up.
    /// </summary>
    /// <remarks>
    /// Sign-in configured, or a connection built on it. Deliberately <i>not</i> "the access probe
    /// succeeded": ambient AWS credentials can resolve on a machine whose owner has never chosen
    /// to use AWS with Connapse, and marking the provider in use on that basis would put work in
    /// front of somebody who asked for none.
    /// </remarks>
    private async Task<HashSet<ConnectionProvider>> InUseProvidersAsync(CancellationToken ct)
    {
        try
        {
            var all = await connections.ListAsync(0, 200, ct);
            return all.Select(c => c.Provider).ToHashSet();
        }
        catch (Exception ex)
        {
            // Worth degrading rather than failing: without this a provider simply looks unused,
            // which is the same as a fresh install and shows an invitation rather than an error.
            logger.LogWarning(ex, "Could not list connections while reading provider setup");
            return [];
        }
    }

    private static bool IsConfigured(AwsSsoSettings s) =>
        !string.IsNullOrEmpty(s.IssuerUrl) && !string.IsNullOrEmpty(s.Region);

    private static bool IsConfigured(AzureAdSettings s) =>
        !string.IsNullOrEmpty(s.ClientId) && !string.IsNullOrEmpty(s.TenantId);

    private static ProviderRequirement SignIn(AwsSsoSettings settings)
    {
        // Matches CloudIdentityService.IsAwsSsoConfigured: both halves, because a region without an
        // issuer URL registers no client and an issuer URL without a region reaches no endpoint.
        bool configured = IsConfigured(settings);

        return new ProviderRequirement(
            "Sign-in",
            "Who can sign into Connapse with AWS, and which cloud identity their search is scoped against.",
            configured ? RequirementStatus.Satisfied : RequirementStatus.NotConfigured,
            configured ? $"{settings.IssuerUrl} ({settings.Region})" : null,
            configured ? "Change" : "Set up",
            "/admin/providers#aws-signin");
    }

    private static ProviderRequirement SignIn(AzureAdSettings settings)
    {
        bool configured = IsConfigured(settings);

        return new ProviderRequirement(
            "Sign-in",
            "Who can sign into Connapse with Azure AD, and which cloud identity their search is scoped against.",
            configured ? RequirementStatus.Satisfied : RequirementStatus.NotConfigured,
            configured ? $"Tenant {settings.TenantId}" : null,
            configured ? "Change" : "Set up",
            "/admin/providers#azure-signin");
    }

    /// <summary>
    /// What Connapse itself can read from AWS.
    /// </summary>
    /// <remarks>
    /// Probed rather than inferred from configuration, because there is no configuration to read:
    /// the SDK resolves credentials from the environment, and the only way to know what it found is
    /// to ask. A static key is reported as a warning rather than a pass — it works, never expires,
    /// and nothing else in the product would ever mention it.
    /// <para>
    /// No role is assumed here. This is the base identity Connapse runs as; a connection naming a
    /// <c>RoleArn</c> narrows from it, and the connection form reports that separately.
    /// </para>
    /// </remarks>
    private async Task<ProviderRequirement> Access(CancellationToken ct)
    {
        const string name = "Access";
        const string description =
            "What Connapse reads as when it syncs an S3 source. Nothing is stored here — "
            + "it uses whatever credentials its environment provides.";

        try
        {
            var identity = await s3Discovery.WhoAmIAsync(ct: ct);

            if (identity is { Succeeded: true, Value: { } who })
            {
                bool weak = who.Kind is AwsCredentialKind.StaticKey or AwsCredentialKind.SsoSession;

                return new ProviderRequirement(
                    name, description,
                    weak ? RequirementStatus.Warning : RequirementStatus.Satisfied,
                    $"{who.Arn} — {Describe(who.Kind)}",
                    "Connections", "/connections");
            }

            return new ProviderRequirement(
                name, description,
                identity.Outcome == AwsProbeOutcome.NoCredentials
                    ? RequirementStatus.NotConfigured
                    : RequirementStatus.Unknown,
                identity.Outcome == AwsProbeOutcome.NoCredentials
                    ? "No credentials are visible to Connapse."
                    : identity.Detail,
                "How to supply credentials", "/connections");
        }
        catch (Exception ex)
        {
            // A status page that throws is worse than one that admits it does not know: the other
            // provider's requirements are still worth showing.
            logger.LogWarning(ex, "Could not read the AWS access requirement");
            return new ProviderRequirement(name, description, RequirementStatus.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Azure's equivalent, not yet probed.
    /// </summary>
    /// <remarks>
    /// Reported as Unknown rather than omitted or assumed satisfied. Azure Blob resolves through
    /// <c>DefaultAzureCredential</c> the same way S3 resolves through the AWS chain, so the same
    /// check is possible — it simply has no equivalent of <see cref="IS3Discovery"/> yet, and
    /// claiming either outcome would be a guess.
    /// </remarks>
    private static ProviderRequirement AzureAccess() =>
        new("Access",
            "What Connapse reads as when it syncs an Azure Blob source. Nothing is stored here — "
            + "it uses managed identity from the environment it runs in.",
            RequirementStatus.Unknown,
            "Connapse does not check this yet. Test an Azure connection to confirm it works.",
            "Connections", "/connections");

    private static string Describe(AwsCredentialKind kind) => kind switch
    {
        AwsCredentialKind.InstanceOrTaskRole => "instance role, which rotates itself",
        AwsCredentialKind.AssumedRole => "assumed role, temporary and scoped",
        AwsCredentialKind.ExternalProcess => "external credential process, no static keys",
        AwsCredentialKind.SsoSession => "SSO session, which expires and needs a browser to renew",
        AwsCredentialKind.StaticKey => "static access key, which never expires",
        _ => "source not recognised"
    };
}
