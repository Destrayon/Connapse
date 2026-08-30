namespace Connapse.Core;

/// <summary>Why a coverage report says what it says.</summary>
/// <remarks>
/// Three states, not two, for the same reason <c>ScopeOutcome</c> has five: "nothing to report" and
/// "could not find out" look identical once flattened, and they need opposite responses. A silent
/// pass on an unreachable AWS is how a warning system teaches people to trust a blank space.
/// </remarks>
public enum CoverageOutcome
{
    /// <summary>This deployment does not filter, so no grant is expected.</summary>
    NotFiltering,

    /// <summary>AWS answered. <c>Ungranted</c> holds whatever nothing covers.</summary>
    Checked,

    /// <summary>AWS could not be asked. Nothing is claimed either way.</summary>
    Unavailable,
}

/// <summary>What is known about the grants covering a connection's buckets.</summary>
/// <param name="Outcome">Whether this was checked at all, and whether the check worked.</param>
/// <param name="Ungranted">
/// Allowed locations no grant touches. Only meaningful when <paramref name="Outcome"/> is
/// <see cref="CoverageOutcome.Checked"/>, and empty in every other case rather than null, so a
/// caller that ignores the outcome reports nothing rather than crashing.
/// </param>
public record GrantCoverageReport(CoverageOutcome Outcome, IReadOnlyList<string> Ungranted)
{
    /// <summary>Whether there is something worth telling an administrator.</summary>
    public bool HasWarning => Outcome is CoverageOutcome.Checked && Ungranted.Count > 0;

    public static readonly GrantCoverageReport NotFiltering =
        new(CoverageOutcome.NotFiltering, []);

    public static readonly GrantCoverageReport Unavailable =
        new(CoverageOutcome.Unavailable, []);
}

/// <summary>
/// Says which of a connection's buckets no access grant reaches.
/// </summary>
/// <remarks>
/// A connection naming an ungranted bucket is not broken in any way Connapse can otherwise detect:
/// it saves, it syncs, it indexes, and every search over it returns nothing to everybody. This
/// exists so that shows up where the bucket is named rather than as an empty result page weeks
/// later.
/// <para>
/// Advisory only. It never prevents a save — authoring a grant for a bucket before the connection
/// that names it exists is the wrong order to work in, and an AWS outage must not stop somebody
/// configuring Connapse.
/// </para>
/// </remarks>
public interface IGrantCoverageReporter
{
    /// <summary>Checks <paramref name="allowedLocations"/> against every grant in the instance.</summary>
    Task<GrantCoverageReport> CheckAsync(
        IEnumerable<string>? allowedLocations, CancellationToken ct = default);
}
