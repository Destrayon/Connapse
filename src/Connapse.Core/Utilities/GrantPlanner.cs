namespace Connapse.Core.Utilities;

/// <summary>
/// Decides which S3 Access Grants to create for a grantee, given the grants they already hold.
/// </summary>
/// <remarks>
/// Pure so it can be tested without AWS, and so the subprefix shape — <c>bucket/prefix/*</c> — has
/// one source shared with <see cref="AccessGrantScript"/>. The writer creates exactly what this
/// returns; if the shape drifted from what the reader parses, a grant just created would still read
/// as ungranted.
/// </remarks>
public static class GrantPlanner
{
    /// <summary>Splits requested locations into those needing a grant and those already covered.</summary>
    /// <param name="requestedLocations">
    /// Buckets, each optionally followed by <c>/</c> and a prefix — the connection's ungranted
    /// locations.
    /// </param>
    /// <param name="existingScopes">
    /// The grantee's current grant scopes as AWS reports them, e.g. <c>s3://bucket/prefix/*</c>.
    /// </param>
    public static GrantPlan Plan(
        IEnumerable<string> requestedLocations, IEnumerable<string> existingScopes)
    {
        var existing = new HashSet<string>(
            (existingScopes ?? []).Select(s => s?.Trim() ?? string.Empty),
            StringComparer.Ordinal);

        var toCreate = new List<string>();
        var alreadyGranted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string location in requestedLocations ?? [])
        {
            string sanitised = AccessGrantScript.SanitiseLocation(location);
            if (sanitised.Length == 0)
                continue;

            // The shape a grant is created and read back as. AWS refuses a grant on the bare
            // s3:// location, so every one names a bucket and the trailing star makes it a subtree.
            string subPrefix = sanitised + "/*";

            if (!seen.Add(subPrefix))
                continue;

            if (existing.Contains("s3://" + subPrefix))
                alreadyGranted.Add(subPrefix);
            else
                toCreate.Add(subPrefix);
        }

        return new GrantPlan(toCreate, alreadyGranted);
    }
}

/// <summary>The outcome of planning grants: what to create, and what already exists.</summary>
public record GrantPlan(IReadOnlyList<string> ToCreate, IReadOnlyList<string> AlreadyGranted);
