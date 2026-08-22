namespace Connapse.Core.Interfaces;

/// <summary>
/// Discovers path-prefix scopes a user is allowed to access within a specific
/// cloud-backed source, based on their linked cloud IAM identity.
/// </summary>
public interface ICloudIdentityProvider
{
    CloudProvider Provider { get; }

    /// <summary>
    /// Returns the set of virtual path prefixes the user is permitted to read.
    /// An empty list means no access. A single "/" means unrestricted access.
    /// </summary>
    /// <param name="connectorConfigJson">
    /// The remote's location as JSON — storage account, bucket, prefix. Previously read off
    /// a container's <c>connector_config</c> column; since #353 that column is gone and the
    /// same fields are recombined from a connection's credential and its source's scope.
    /// Passed as JSON rather than a typed record because each provider needs different
    /// fields and only that provider knows which.
    /// </param>
    Task<CloudScopeResult> DiscoverScopesAsync(
        CloudIdentityData identityData,
        string? connectorConfigJson,
        CancellationToken ct = default);
}
