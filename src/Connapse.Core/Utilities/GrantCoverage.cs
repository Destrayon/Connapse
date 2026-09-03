namespace Connapse.Core.Utilities;

/// <summary>
/// Works out which of a connection's allowed locations no S3 access grant reaches.
/// </summary>
/// <remarks>
/// Per-user filtering hides every document whose resource URI no grant of the signed-in person's
/// covers. A connection naming a bucket nothing is granted on therefore syncs perfectly, indexes
/// everything, and returns nothing to anybody — and looks identical to a correctly configured one
/// the reader simply has no access to. This is what lets that be said out loud at the moment the
/// bucket is named.
/// <para>
/// Pure, and deliberately unaware of AWS. The grant scopes come from the caller, which keeps the
/// matching testable against the shapes AWS actually returns rather than against a mock.
/// </para>
/// </remarks>
public static class GrantCoverage
{
    private const string Scheme = "s3://";

    /// <summary>
    /// The allowed locations no grant scope touches, in the order given.
    /// </summary>
    /// <param name="allowedLocations">
    /// Connection entries, each a bucket optionally followed by <c>/</c> and a prefix — the form
    /// <see cref="StorageLocationPolicy"/> reads.
    /// </param>
    /// <param name="grantScopes">
    /// Every grant scope in the Access Grants instance, as <c>s3://bucket/prefix*</c>.
    /// </param>
    /// <remarks>
    /// Overlap rather than containment. A grant on <c>s3://bucket/team/*</c> against an allowed
    /// location of <c>bucket</c> reaches part of it, which is a legitimate arrangement — one team
    /// sees their prefix — and reporting it would train an administrator to ignore this. Only a
    /// location no grant touches at all is worth saying anything about, because that one can never
    /// return a result to anyone.
    /// </remarks>
    public static IReadOnlyList<string> Ungranted(
        IEnumerable<string>? allowedLocations, IEnumerable<string>? grantScopes)
    {
        var locations = (allowedLocations ?? [])
            .Select(l => l?.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => l!)
            .ToList();

        if (locations.Count == 0)
            return [];

        var scopes = (grantScopes ?? [])
            .Select(Normalise)
            .Where(s => s.Length > 0)
            .ToList();

        return [.. locations.Where(l => !scopes.Any(s => Overlaps(s, Normalise(Scheme + l))))];
    }

    /// <summary>
    /// Whether <paramref name="grantScope"/> still reaches any of <paramref name="allowedLocations"/>.
    /// </summary>
    /// <remarks>
    /// The reverse of <see cref="Ungranted"/>: that asks which locations no grant reaches; this asks
    /// whether one grant reaches any location — the question cleanup asks to decide a grant is
    /// orphaned. Same boundary-aware overlap, one source of truth, so "granted" and "orphaned"
    /// cannot disagree. A scope no location covers is orphaned.
    /// </remarks>
    public static bool IsScopeCovered(string? grantScope, IEnumerable<string>? allowedLocations)
    {
        string scope = Normalise(grantScope);
        if (scope.Length == 0)
            return false;

        return (allowedLocations ?? [])
            .Select(l => l?.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .Any(l => Overlaps(scope, Normalise(Scheme + l!)));
    }

    /// <summary>
    /// Reduces a scope or location to the URI prefix it stands for.
    /// </summary>
    /// <remarks>
    /// The trailing <c>*</c> is what makes a grant a subtree rather than one object, and both forms
    /// reduce to the same prefix here: an object grant is exactly the point where a location and a
    /// grant still touch, so treating it as a prefix is right for this question even though it is
    /// wrong for deciding what a search may read. That decision lives in <c>GrantScope</c>, which
    /// keeps the distinction.
    /// </remarks>
    private static string Normalise(string? scope)
    {
        string trimmed = scope?.Trim() ?? string.Empty;

        if (trimmed.EndsWith('*'))
            trimmed = trimmed[..^1];

        // A bare bucket and a bucket with a trailing slash name the same subtree, and AWS returns
        // both shapes depending on how the grant was written.
        return trimmed.TrimEnd('/');
    }

    /// <summary>Whether either prefix contains the other.</summary>
    /// <remarks>
    /// Compared on a boundary rather than as raw text, so <c>s3://logs</c> does not appear to reach
    /// <c>s3://logs-archive</c> — a bucket whose name merely starts the same way is somebody else's
    /// data, and treating it as covered would report a connection as fine when nobody can read it.
    /// </remarks>
    private static bool Overlaps(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return false;

        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length <= b.Length ? b : a;

        if (!longer.StartsWith(shorter, StringComparison.Ordinal))
            return false;

        return longer.Length == shorter.Length || longer[shorter.Length] == '/';
    }
}
