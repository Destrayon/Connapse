using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Reports what each cloud provider still needs.
/// </summary>
/// <remarks>
/// Reads from the settings store and from AWS itself. The one thing it writes is a timestamp
/// recording that a stored credential was honoured — an observation about a credential, not a
/// credential — because nothing else is positioned to notice. It still stores no secret, which is
/// the property that stops the page it feeds turning into a place credentials live.
/// </remarks>
public class ProviderSetupReader(
    IOptionsMonitor<AzureAdSettings> azureAd,
    IOptionsMonitor<CognitoSettings> cognito,
    IS3Discovery s3Discovery,
    IConnectionStore connections,
    IProviderCredentialStore credentials,
    TimeProvider clock,
    ILogger<ProviderSetupReader> logger) : IProviderSetupReader
{
    /// <summary>
    /// How long a stored key may fail before that stops being propagation delay.
    /// </summary>
    /// <remarks>
    /// IAM is eventually consistent and AWS gives no ceiling on how long a new access key takes to
    /// become usable. An hour is far past any propagation anyone observes, which is the point: the
    /// cost of waiting too long is a page that says "provisioning" for a while, and the cost of
    /// giving up too early is telling an administrator their setup failed when it had not.
    /// </remarks>
    public static readonly TimeSpan ProvisioningWindow = TimeSpan.FromHours(1);

    /// <summary>The sign-in form's section on the provider page.</summary>
    /// <remarks>
    /// A fragment rather than a path, which the page renders as a scroll rather than a link. Both
    /// href forms are wrong for a same-page target here: the full path is intercepted by Blazor's
    /// router and re-navigated without scrolling, and a bare fragment resolves against
    /// <c>&lt;base href="/"&gt;</c> and lands on the home page.
    /// </remarks>
    private const string SignInSection = "#signin";

    /// <summary>The Cognito form's section on the AWS provider page.</summary>
    /// <remarks>A fragment, for the same reason as <see cref="SignInSection"/>.</remarks>
    private const string PermissionsSection = "#permissions";

    public async Task<IReadOnlyList<ProviderSetup>> ReadAsync(CancellationToken ct = default)
    {
        var providers = await InUseProvidersAsync(ct);

        return
        [
            new ProviderSetup("aws", "AWS",
                [await Access(ct), PerUserPermissions(cognito.CurrentValue)],
                InUse: providers.Contains(ConnectionProvider.S3)),

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
    /// <para>
    /// Every page, not the first. <c>ListAsync</c> pages, so a single call read the first 200
    /// connections and stopped — and the answer this produces is a boolean per provider, so one
    /// S3 connection sorting past the cutoff was enough to report AWS as unused and hide its
    /// requirements behind an invitation to set it up.
    /// </para>
    /// </remarks>
    private async Task<HashSet<ConnectionProvider>> InUseProvidersAsync(CancellationToken ct)
    {
        const int pageSize = 200;

        try
        {
            var found = new HashSet<ConnectionProvider>();

            for (int skip = 0; ; skip += pageSize)
            {
                var page = await connections.ListAsync(skip, pageSize, ct);
                if (page.Count == 0) break;

                foreach (var connection in page)
                    found.Add(connection.Provider);

                // A short page is the last one. Asking again would be a query guaranteed to return
                // nothing on every installation that fits in one page, which is most of them.
                if (page.Count < pageSize) break;
            }

            return found;
        }
        catch (Exception ex)
        {
            // Worth degrading rather than failing: without this a provider simply looks unused,
            // which is the same as a fresh install and shows an invitation rather than an error.
            logger.LogWarning(ex, "Could not list connections while reading provider setup");
            return [];
        }
    }

    private static bool IsConfigured(AzureAdSettings s) =>
        !string.IsNullOrEmpty(s.ClientId) && !string.IsNullOrEmpty(s.TenantId);

    private static ProviderRequirement SignIn(AzureAdSettings settings)
    {
        bool configured = IsConfigured(settings);

        return new ProviderRequirement(
            "Sign-in",
            "Who can sign into Connapse with Azure AD, and which cloud identity their search is scoped against.",
            configured ? RequirementStatus.Satisfied : RequirementStatus.NotConfigured,
            configured ? $"Tenant {settings.TenantId}" : null,
            configured ? "Change" : "Set up",
            SignInSection);
    }

    /// <summary>
    /// Whether people can connect an AWS identity of their own.
    /// </summary>
    /// <remarks>
    /// Reports on the pool being configured, not on filtering working. Those are different claims,
    /// and only the first is this page's to make: search is not scoped by cloud permissions yet
    /// (#421), so a requirement worded around results would be green while every user still sees
    /// everything.
    /// <para>
    /// Warning rather than <see cref="RequirementStatus.NotConfigured"/> when it is unset, because
    /// the status a provider shows is its weakest requirement. NotConfigured would summarise the
    /// whole of AWS as unconfigured on an installation whose S3 syncing works perfectly, and an
    /// installation that never wants per-user scoping has made a choice rather than left a job
    /// half done. Warning says the accurate thing: AWS works, and nobody is scoped.
    /// </para>
    /// </remarks>
    private static ProviderRequirement PerUserPermissions(CognitoSettings settings)
    {
        const string name = "Per-user permissions";
        const string description =
            "The Cognito user pool people connect their AWS identity through, so their results "
            + "can be scoped to what that identity may read.";

        if (!settings.IsConfigured)
            return new ProviderRequirement(name, description,
                RequirementStatus.Warning,
                "Not set up, so nobody can connect an AWS identity.",
                "Set up", PermissionsSection);

        return new ProviderRequirement(name, description,
            RequirementStatus.Satisfied, settings.IssuerUrl, "Change", PermissionsSection);
    }

    /// <summary>
    /// Whether the AWS identity works. Ready, still coming up, or not.
    /// </summary>
    /// <remarks>
    /// Binary on purpose. This answers one question — can Connapse read S3 — and the ARN is the
    /// only supporting fact worth a line, because it is the one an administrator needs when the
    /// answer is no. Bucket counts and credential provenance are not that; they were narration.
    /// <para>
    /// Ready means a call that IAM actually evaluates came back. <c>sts:GetCallerIdentity</c> is
    /// not one: it answers for any valid credential no matter what that credential may do, so
    /// asking it alone reported a green tick for an identity whose policy had never attached.
    /// </para>
    /// <para>
    /// The rest of the shape follows from who created the credential. Connapse knows what it
    /// granted its own key and when, so it can say whether that key is late or broken. It knows
    /// neither about a credential the environment supplied, so it does not pass judgement on one.
    /// </para>
    /// <para>
    /// No role is assumed here. This is the base identity Connapse runs as; a connection naming a
    /// <c>RoleArn</c> narrows from it, and the connection form reports that separately.
    /// </para>
    /// </remarks>
    private async Task<ProviderRequirement> Access(CancellationToken ct)
    {
        const string name = "Access";
        const string description = "Whether Connapse can read S3.";

        try
        {
            var stored = await StoredCredentialAsync(ct);
            var identity = await s3Discovery.WhoAmIAsync(ct: ct);

            if (identity is { Succeeded: true, Value: { } who })
            {
                var buckets = await s3Discovery.ListBucketsAsync(ct: ct);

                if (buckets.Succeeded)
                {
                    // Remembered, because age alone cannot tell "not working yet" from "not working
                    // any more". Everything below turns on whether this credential has ever worked.
                    if (stored is not null)
                        await MarkVerifiedAsync(ct);

                    return new ProviderRequirement(name, description,
                        RequirementStatus.Satisfied, who.Arn, "Connections", "/connections");
                }

                // A credential Connapse did not create is not Connapse's to judge. One an operator
                // scoped to named buckets lacks s3:ListAllMyBuckets by design and syncs perfectly;
                // it simply cannot demonstrate itself from here, and calling that broken would be
                // wrong about a working installation.
                if (stored is null)
                    return new ProviderRequirement(name, description,
                        RequirementStatus.Warning,
                        $"{who.Arn} — Connapse cannot confirm what this reaches. Test a connection.",
                        "Connections", "/connections");

                return NotWorkingYet(name, description, stored,
                    $"{who.Arn} authenticates but cannot read S3.");
            }

            // Credentials that do not resolve at all, with a key stored, is the same story one step
            // earlier: AWS has not started honouring the key yet.
            if (stored is not null)
                return NotWorkingYet(name, description, stored,
                    "The stored access key is not being accepted by AWS.");

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
    /// Splits "not working yet" from "not working", on the age of the stored key.
    /// </summary>
    /// <remarks>
    /// IAM is eventually consistent, and the window in which a new key is refused is the same window
    /// in which the administrator who just created it is looking at this page. Showing a failure
    /// there sends someone to redo work that was about to succeed on its own.
    /// </remarks>
    private ProviderRequirement NotWorkingYet(
        string name, string description, ProviderCredentialInfo stored, string reason)
    {
        const string action = "Set up access";

        // A bare fragment, not the full path. This requirement is only ever rendered on the AWS
        // provider page, and Blazor's router intercepts a same-page href and re-navigates without
        // scrolling — so "/admin/providers/aws#access" looked like a link that did nothing. The
        // browser handles a fragment-only href itself.
        const string href = "#access";

        // A credential that has worked is not waiting to start working. Something removed it --
        // the IAM user deleted, the key deactivated -- and offering to keep waiting for it is the
        // page telling someone to sit still while nothing happens.
        if (stored.VerifiedAt is not null)
            return new ProviderRequirement(name, description, RequirementStatus.Failed,
                "This key worked before and no longer does. It has most likely been deleted or "
                + "deactivated in AWS. Create the identity again.", action, href);

        TimeSpan age = clock.GetUtcNow() - new DateTimeOffset(
            DateTime.SpecifyKind(stored.CreatedAt, DateTimeKind.Utc));

        if (age < ProvisioningWindow)
            return new ProviderRequirement(name, description, RequirementStatus.Provisioning,
                "AWS has not finished issuing this key. This usually takes seconds.");

        return new ProviderRequirement(name, description, RequirementStatus.Failed,
            $"{reason} Create the identity again.", action, href);
    }

    /// <summary>Records a successful call, without letting a failed write break the page.</summary>
    private async Task MarkVerifiedAsync(CancellationToken ct)
    {
        try
        {
            await credentials.MarkVerifiedAsync("aws", clock.GetUtcNow().UtcDateTime, ct);
        }
        catch (Exception ex)
        {
            // Losing this costs a wrong message on a later failure. Losing the status page costs
            // every message on it, so this is the one that gets swallowed.
            logger.LogWarning(ex, "Could not record that the AWS credential was verified");
        }
    }

    /// <summary>The credential Connapse stores for AWS, or null when it is using the environment's.</summary>
    /// <remarks>
    /// Absence is a legitimate state, and so is a key ring that can no longer decrypt one. Neither
    /// should take the whole status page down, so both come back as "there is no stored key" and the
    /// probe results speak for themselves.
    /// </remarks>
    private async Task<ProviderCredentialInfo?> StoredCredentialAsync(CancellationToken ct)
    {
        try
        {
            return await credentials.GetAsync("aws", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the stored AWS credential");
            return null;
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


}
