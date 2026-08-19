namespace Connapse.Core.Utilities;

/// <summary>
/// Decides whether a source's storage scope falls inside the locations its connection permits.
/// <para>
/// A cloud connection holds only a credential and an endpoint, and the source names its own
/// bucket or blob container — so without this, anyone who can create a source may point it at
/// anything the connection's IAM role or managed identity can read, and have it ingested and
/// made searchable. IAM cannot tell one source from another here, because a single connection
/// role is shared across every source that uses it.
/// </para>
/// <para>
/// This mirrors Snowflake's <c>STORAGE_ALLOWED_LOCATIONS</c>, where an admin-created storage
/// integration holds the role ARN and separately limits which buckets a user-created stage may
/// reference. Snowflake ships it alongside narrow IAM rather than instead of it, and so should
/// this: the allowlist is application config, while IAM is what actually stops a bug here.
/// </para>
/// </summary>
public static class StorageLocationPolicy
{
    /// <summary>
    /// Evaluates a source's <paramref name="container"/> (an S3 bucket or Azure blob container)
    /// and optional <paramref name="prefix"/> against the connection's allowed locations.
    /// </summary>
    public static StorageLocationDecision Evaluate(
        IReadOnlyList<string> allowedLocations, string container, string? prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        var entries = allowedLocations
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();

        if (entries.Count == 0)
            return StorageLocationDecision.UnrestrictedByConfiguration;

        string candidate = Normalize($"{container}/{prefix?.TrimStart('/') ?? string.Empty}");

        foreach (string entry in entries)
        {
            string allowed = Normalize(entry);

            // An entry naming only a bucket permits any prefix within it; an entry naming a
            // bucket and prefix permits only that subtree. Compared with a trailing slash so
            // "bucket/docs" cannot admit "bucket/docs-internal" — the same off-by-slash trap
            // that bites prefix-based filesystem checks.
            if (candidate.Equals(allowed, StringComparison.Ordinal) ||
                candidate.StartsWith(allowed.EndsWith('/') ? allowed : allowed + "/", StringComparison.Ordinal))
            {
                return StorageLocationDecision.Allowed;
            }
        }

        return StorageLocationDecision.Denied;
    }

    /// <summary>
    /// Collapses a location to a comparable form. Bucket and container names are
    /// case-sensitive in both S3 and Azure, so this never changes case.
    /// </summary>
    private static string Normalize(string location) =>
        location.Replace('\\', '/').Trim('/');
}

public enum StorageLocationDecision
{
    /// <summary>The scope falls inside a permitted location.</summary>
    Allowed,

    /// <summary>The connection lists no locations. Permitted, but the caller should warn.</summary>
    UnrestrictedByConfiguration,

    /// <summary>The connection lists locations and this scope is not within any of them.</summary>
    Denied
}
