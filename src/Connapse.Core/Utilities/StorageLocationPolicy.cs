using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <summary>The property every connection stores its allowlist under.</summary>
    public const string PropertyName = "allowedLocations";

    /// <summary>
    /// Reads a connection's allowlist, returning <c>null</c> when the property is <b>absent</b>.
    /// </summary>
    /// <remarks>
    /// The distinction this exists to preserve is between an <i>absent</i> control and a
    /// <i>broken</i> one. Only the first fails open, and only for one release.
    /// <para>
    /// So anything present but unusable — a non-string element, a value that is not an array at
    /// all — becomes a blank entry rather than being dropped. Dropping it shrinks the list back
    /// to empty, which is indistinguishable from absent, and a typo becomes an open door.
    /// </para>
    /// <para>
    /// Both the sync-time enforcement point and the create-time preflight call this. They used
    /// to parse the allowlist separately, with different rules — the enforcement copy silently
    /// filtered non-strings out, so <c>[42]</c> was refused in the form and permitted at sync,
    /// which is the wrong way round for the two to disagree.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string>? ReadAllowedLocations(
        JsonElement credential, string name = PropertyName)
    {
        // A configuration that is not an object at all — "[]", a bare string, a number — cannot
        // be said to omit anything. Returning null there would read as "declares no allowlist"
        // and take the grace path, which is the same fail-open this reader exists to close,
        // one level further out.
        if (credential.ValueKind != JsonValueKind.Object)
            return [string.Empty];

        if (!credential.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind != JsonValueKind.Array)
            return [string.Empty];

        return [.. value.EnumerateArray().Select(e =>
            e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty)];
    }

    /// <inheritdoc cref="ReadAllowedLocations(JsonElement, string)"/>
    public static IReadOnlyList<string>? ReadAllowedLocations(
        JsonObject? credential, string name = PropertyName)
    {
        // Null here means the configuration was blank or unparseable, which the callers already
        // handle — a JsonObject that exists is by definition an object, so the non-object case
        // the JsonElement overload guards against cannot arise on this side.
        if (credential is null || !credential.TryGetPropertyValue(name, out var node))
            return null;

        // An explicit JSON null is present-but-unusable, not absent — the same answer the
        // JsonElement overload gives, and the safe one. Reading it as absent would fail open,
        // and the two overloads disagreeing is the drift this shared reader exists to end.
        if (node is not JsonArray array)
            return [string.Empty];

        return [.. array.Select(e =>
            e is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty)];
    }

    /// <summary>
    /// Evaluates a source's <paramref name="container"/> (an S3 bucket or Azure blob container)
    /// and optional <paramref name="prefix"/> against the connection's allowed locations.
    /// </summary>
    /// <param name="allowedLocations">
    /// <c>null</c> when the connection declares no allowlist at all — the grace path. An empty
    /// or all-blank list means one was declared and permits nothing, which is refused.
    /// </param>
    public static StorageLocationDecision Evaluate(
        IReadOnlyList<string>? allowedLocations, string container, string? prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        // Declaring nothing is the grace path. Declaring an allowlist that permits nothing is
        // not: that is a malformed or empty allowlist, and reading it as "no restrictions"
        // would turn a typo into an open door. Only an absent control fails open.
        if (allowedLocations is null)
            return StorageLocationDecision.UnrestrictedByConfiguration;

        var entries = allowedLocations
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();

        if (entries.Count == 0)
            return StorageLocationDecision.Denied;

        string candidate = Normalize($"{container}/{prefix?.TrimStart('/') ?? string.Empty}");

        foreach (string entry in entries)
        {
            string allowed = Normalize(entry);

            // An entry naming only a bucket permits any prefix within it; an entry naming a
            // bucket and prefix permits only that subtree. Compared with a trailing slash so
            // "bucket/docs" cannot admit "bucket/docs-internal" — the same off-by-slash trap
            // that bites prefix-based filesystem checks. Normalize has already stripped any
            // trailing slash, so the separator is always appended here.
            if (candidate.Equals(allowed, StringComparison.Ordinal) ||
                candidate.StartsWith(allowed + "/", StringComparison.Ordinal))
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
