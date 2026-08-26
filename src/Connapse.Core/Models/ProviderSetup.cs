namespace Connapse.Core;

/// <summary>Whether one requirement of a provider has been met.</summary>
public enum RequirementStatus
{
    /// <summary>Connapse could not tell — usually because checking it needs a call that failed.</summary>
    Unknown = 0,

    /// <summary>Nothing has been set up. The normal starting state, not a fault.</summary>
    NotConfigured = 1,

    /// <summary>Met.</summary>
    Satisfied = 2,

    /// <summary>Working, but resting on something worth improving — a static key, an expiring session.</summary>
    Warning = 3
}

/// <summary>
/// One thing a provider needs before it is usable.
/// </summary>
/// <param name="Name">Short label — "Sign-in", "Access".</param>
/// <param name="Description">What it governs, in the reader's terms rather than the system's.</param>
/// <param name="Status">Whether it is met.</param>
/// <param name="Detail">
/// The specific finding: the ARN Connapse resolves to, the issuer URL configured, the reason a
/// check could not run. Null when there is nothing to add beyond the status.
/// </param>
/// <param name="ActionLabel">Wording for the link that addresses this, if there is one.</param>
/// <param name="ActionHref">Where that link goes.</param>
public record ProviderRequirement(
    string Name,
    string Description,
    RequirementStatus Status,
    string? Detail = null,
    string? ActionLabel = null,
    string? ActionHref = null);

/// <summary>
/// A cloud provider and everything Connapse needs from it.
/// </summary>
/// <remarks>
/// Setting up AWS is two jobs — configure sign-in, arrange access — and they read as unrelated
/// because they live on different pages. They are usually done in one sitting with the same console
/// open, so this gathers them.
/// <para>
/// A view over other stores, holding nothing itself. Requirements are a list rather than fixed
/// fields because what a provider needs varies: adding one later should be a list entry, not a
/// change to the page that renders it.
/// </para>
/// <para>
/// It must not grow storage of its own. A provider that holds credentials is a service-account
/// concept sitting above <c>Connection</c>, which already <i>is</i> the credential boundary — and
/// neither Airbyte nor Fivetran has such a layer, because the configured source carries its own
/// authentication. See docs/plans/providers-page.md.
/// </para>
/// </remarks>
/// <param name="Key">Stable identifier — "aws", "azure".</param>
/// <param name="DisplayName">What the administrator calls it.</param>
/// <param name="Requirements">Everything this provider needs, in the order to read them.</param>
/// <param name="InUse">
/// Whether this installation has taken this provider up at all — sign-in configured, or a
/// connection built on it.
/// <para>
/// The distinction the page turns on. A provider nobody uses has no outstanding work: reporting
/// Azure as "not set up" to somebody who has no Azure states a problem where there is only an
/// option they declined. Requirements are worth reading for the clouds you actually use, and
/// noise for the rest.
/// </para>
/// </param>
public record ProviderSetup(
    string Key,
    string DisplayName,
    IReadOnlyList<ProviderRequirement> Requirements,
    bool InUse = false)
{
    /// <summary>
    /// The provider's overall state, taken from its weakest requirement.
    /// </summary>
    /// <remarks>
    /// Weakest rather than an average or a count. A provider whose sign-in works and whose access
    /// does not is not half ready — it is not ready, and summarising it as "1 of 2" invites the
    /// reader to feel finished.
    /// </remarks>
    public RequirementStatus Overall
    {
        get
        {
            if (Requirements.Count == 0) return RequirementStatus.Unknown;

            // Ordered by how much attention each deserves, worst first.
            foreach (var worst in new[]
                     {
                         RequirementStatus.NotConfigured,
                         RequirementStatus.Unknown,
                         RequirementStatus.Warning
                     })
            {
                if (Requirements.Any(r => r.Status == worst))
                    return worst;
            }

            return RequirementStatus.Satisfied;
        }
    }
}

/// <summary>Builds the current picture for each provider.</summary>
public interface IProviderSetupReader
{
    Task<IReadOnlyList<ProviderSetup>> ReadAsync(CancellationToken ct = default);
}
