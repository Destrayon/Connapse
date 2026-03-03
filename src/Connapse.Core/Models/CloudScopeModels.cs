namespace Connapse.Core;

/// <summary>
/// Result of a scope discovery operation for a user accessing a cloud container.
/// </summary>
public record CloudScopeResult(
    bool HasAccess,
    IReadOnlyList<string> AllowedPrefixes,
    string? Error = null)
{
    public static CloudScopeResult Deny(string reason) =>
        new(false, [], reason);

    public static CloudScopeResult Allow(IReadOnlyList<string> prefixes) =>
        new(true, prefixes);

    public static CloudScopeResult FullAccess() =>
        new(true, ["/"]);

    /// <summary>
    /// Returns true if the given path falls within any of the allowed prefixes.
    /// Always true when AllowedPrefixes contains "/".
    /// </summary>
    public bool IsPathAllowed(string path) =>
        HasAccess && AllowedPrefixes.Any(prefix =>
            prefix == "/" || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
