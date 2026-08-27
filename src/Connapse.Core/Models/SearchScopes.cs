namespace Connapse.Core;

/// <summary>
/// What a search is allowed to reach, as resource-URI prefixes.
/// </summary>
/// <remarks>
/// Prefixes rather than document ids. A user's grants are a handful of locations —
/// <c>s3://bucket/team/</c> — while the documents inside them are unbounded, so matching prefixes
/// costs a predicate whose size is the number of grants and an id list costs one whose size is the
/// corpus.
/// <para>
/// <see cref="Unrestricted"/> and an empty <see cref="Matches"/> are opposites and must never
/// be confused: the first is "this deployment does not filter", the second is "this user reaches
/// nothing". A type that represented both as an empty list would turn a misconfiguration into an
/// open door, which is the failure this distinction exists to prevent.
/// </para>
/// </remarks>
/// <summary>Why a search may reach what it may.</summary>
/// <remarks>
/// Three of these mean "nothing", and they are kept apart because they send whoever investigates to
/// three different places. <see cref="NoGrants"/> is almost always configuration — a deployment
/// with no S3 Access Grants instance returns an empty list for every user, which is
/// indistinguishable from denial unless something says so.
/// </remarks>
public enum ScopeOutcome
{
    /// <summary>This deployment does not filter.</summary>
    Unrestricted,

    /// <summary>The user has grants, and they are in <c>Matches</c>.</summary>
    Granted,

    /// <summary>The user was resolved and has no grants.</summary>
    NoGrants,

    /// <summary>The caller could not be resolved to a person.</summary>
    NoPrincipal,

    /// <summary>Permissions could not be determined. Denies, deliberately.</summary>
    ResolverFailed,
}

public sealed record SearchScopes
{
    private SearchScopes(bool unrestricted, IReadOnlyList<GrantMatch> matches, ScopeOutcome outcome)
    {
        IsUnrestricted = unrestricted;
        Matches = matches;
        Outcome = outcome;
    }

    /// <summary>
    /// No filtering: every document is reachable.
    /// </summary>
    /// <remarks>
    /// The state of a deployment that has not configured per-user permissions, which is every
    /// deployment until a resolver exists. Filtering is opt-in because denying by default here
    /// would leave every existing installation unable to search anything after an upgrade.
    /// </remarks>
    public static readonly SearchScopes Unrestricted =
        new(true, [], ScopeOutcome.Unrestricted);

    /// <summary>Nothing is reachable. A resolved user with no grants.</summary>
    public static readonly SearchScopes None =
        new(false, [], ScopeOutcome.NoGrants);

    /// <summary>Nothing is reachable, because nobody could be named.</summary>
    public static readonly SearchScopes NoPrincipal =
        new(false, [], ScopeOutcome.NoPrincipal);

    /// <summary>Nothing is reachable, because permissions could not be determined.</summary>
    /// <remarks>
    /// Failing closed. XACML 3.0 §7.2.2: a deny-biased enforcement point denies without an explicit
    /// permit, and an indeterminate answer is not a permit. The caller is told this is an error
    /// rather than an empty result, because they are not the same thing.
    /// </remarks>
    public static readonly SearchScopes Failed =
        new(false, [], ScopeOutcome.ResolverFailed);

    /// <summary>Only documents matching one of these rules.</summary>
    public static SearchScopes Of(IReadOnlyList<GrantMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);

        var usable = matches.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList();
        return usable.Count == 0
            ? None
            : new SearchScopes(false, usable, ScopeOutcome.Granted);
    }

    /// <summary>Only documents whose resource URI starts with one of these.</summary>
    public static SearchScopes Of(IReadOnlyList<string> uriPrefixes)
    {
        ArgumentNullException.ThrowIfNull(uriPrefixes);

        return Of(uriPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new GrantMatch(p, IsExact: false))
            .ToList());
    }

    public bool IsUnrestricted { get; }

    /// <summary>The rules a document's resource URI must satisfy one of.</summary>
    public IReadOnlyList<GrantMatch> Matches { get; }

    /// <summary>Why this scope set is what it is.</summary>
    public ScopeOutcome Outcome { get; }

    /// <summary>True when this permits nothing, so a query need not run at all.</summary>
    public bool IsEmpty => !IsUnrestricted && Matches.Count == 0;

    /// <summary>The character that turns a wildcard back into a literal.</summary>
    /// <remarks>
    /// Not backslash. Postgres accepts it, but it collides with string-literal escaping in enough
    /// contexts to be worth avoiding when the choice is free.
    /// </remarks>
    public const char LikeEscape = '!';

    /// <summary>
    /// Turns a URI prefix into a <c>LIKE</c> pattern that matches it literally.
    /// </summary>
    /// <remarks>
    /// <c>%</c> and <c>_</c> are wildcards to <c>LIKE</c>, and a grant is not a pattern — it is a
    /// location. Without this a grant for <c>s3://acme/team_docs/</c> also matches
    /// <c>s3://acme/teamXdocs/</c>, because <c>_</c> matches any single character. That is a user
    /// reading a prefix nobody granted them, and underscores in S3 key prefixes are ordinary.
    /// <para>
    /// Here rather than in each store, because the escape character chosen in the pattern and the
    /// one named in the <c>ESCAPE</c> clause have to agree, and they are written in two files.
    /// </para>
    /// </remarks>
    public static string ToLikePattern(string uriPrefix)
    {
        ArgumentNullException.ThrowIfNull(uriPrefix);

        // The escape character first, or escaping the wildcards would re-escape its own output.
        return uriPrefix
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal)
            + "%";
    }
}

/// <summary>
/// Works out what a given user may reach.
/// </summary>
/// <remarks>
/// The seam between where permissions come from and how they are enforced. Enforcement is the
/// expensive, risky half — a predicate in two hand-written SQL queries — and it does not care
/// whether the answer came from a cloud provider, a table, or a test.
/// <para>
/// Implementations must not cache an allow across requests without a way to invalidate it. A stale
/// allow is a user reading what they no longer may; a stale deny is a support ticket.
/// </para>
/// </remarks>
public interface ISearchScopeResolver
{
    /// <param name="userId">
    /// Who is searching, or null when the caller could not be resolved to a person.
    /// </param>
    Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default);
}
