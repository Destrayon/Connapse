using Connapse.Core;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Answers which of a connection's buckets no access grant reaches.
/// </summary>
/// <remarks>
/// Reads every grant scope in the instance once and matches locally, because a page listing
/// connections asks this question several times over and the answer is the same for all of them.
/// <para>
/// The cache lifetime is how long a newly authored grant keeps being reported as missing. Kept the
/// same as the search resolver's, so an administrator who creates a grant and refreshes sees both
/// the warning clear and the search start working at the same moment rather than one before the
/// other.
/// </para>
/// </remarks>
public sealed class GrantCoverageReporter(
    IAccessGrantsReader grants,
    IOptionsMonitor<SamlSignInSettings> samlSignIn,
    IMemoryCache cache,
    ILogger<GrantCoverageReporter> logger) : IGrantCoverageReporter
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private const string CacheKey = "aws-all-grant-scopes";

    /// <inheritdoc />
    public async Task<GrantCoverageReport> CheckAsync(
        IEnumerable<string>? allowedLocations, CancellationToken ct = default)
    {
        // Filtering is opt-in, and an installation that has not opted in expects no grants at all.
        // Warning there would put a permanent complaint on every S3 connection in every deployment
        // that does not use this feature.
        if (!samlSignIn.CurrentValue.IsConfigured)
            return GrantCoverageReport.NotFiltering;

        IReadOnlyList<string> scopes;
        try
        {
            if (!cache.TryGetValue(CacheKey, out IReadOnlyList<string>? cached) || cached is null)
            {
                cached = await grants.ListAllScopesAsync(ct);
                cache.Set(CacheKey, cached, CacheLifetime);
            }

            scopes = cached;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Says nothing rather than warning. A failure here means the grants are unknown, and
            // reporting every bucket as ungranted on an outage would send somebody to author grants
            // that already exist.
            logger.LogWarning(ex, "Could not read access grant scopes; not reporting coverage");
            return GrantCoverageReport.Unavailable;
        }

        return new GrantCoverageReport(
            CoverageOutcome.Checked, GrantCoverage.Ungranted(allowedLocations, scopes));
    }
}
