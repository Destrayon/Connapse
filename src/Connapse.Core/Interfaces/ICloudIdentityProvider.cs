namespace Connapse.Core.Interfaces;

/// <summary>
/// Discovers path-prefix scopes a user is allowed to access within a specific
/// cloud connector container, based on their linked cloud IAM identity.
/// </summary>
public interface ICloudIdentityProvider
{
    CloudProvider Provider { get; }

    /// <summary>
    /// Returns the set of virtual path prefixes the user is permitted to read.
    /// An empty list means no access. A single "/" means unrestricted access.
    /// </summary>
    Task<CloudScopeResult> DiscoverScopesAsync(
        CloudIdentityData identityData,
        Container container,
        CancellationToken ct = default);
}
