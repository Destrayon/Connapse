namespace Connapse.Core.Interfaces;

/// <summary>
/// Orchestrates cloud scope discovery: cache check, identity lookup, provider dispatch.
/// </summary>
public interface ICloudScopeService
{
    /// <summary>
    /// Resolves the scope result for a user accessing a container.
    /// Returns null if the container type does not require scope enforcement
    /// (MinIO, Filesystem, InMemory — these use role-level RBAC only).
    /// </summary>
    Task<CloudScopeResult?> GetScopesAsync(
        Guid userId,
        Container container,
        CancellationToken ct = default);
}
