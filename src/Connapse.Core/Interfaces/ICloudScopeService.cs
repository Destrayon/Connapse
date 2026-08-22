namespace Connapse.Core.Interfaces;

/// <summary>
/// Orchestrates cloud scope discovery: cache check, identity lookup, provider dispatch.
/// </summary>
public interface ICloudScopeService
{
    /// <summary>
    /// Resolves the scope result for a user reading a source, or null when the source's
    /// provider needs no scope enforcement — a Filesystem source is local, so role-level
    /// RBAC is the whole story.
    /// <para>
    /// Takes a source rather than a container since #353. Containers are managed storage
    /// only, and managed storage is Connapse's own backend: there is no external IAM to
    /// consult and never was. Leaving this pointed at containers would have made it
    /// unconditionally return null — enforcement that reads as present but cannot fire.
    /// </para>
    /// </summary>
    Task<CloudScopeResult?> GetScopesAsync(
        Guid userId,
        Source source,
        Connection connection,
        CancellationToken ct = default);
}
