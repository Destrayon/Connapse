using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// The regions Connapse has S3 data in, read from the configured connections.
/// </summary>
/// <remarks>
/// Grants live in the Access Grants instance in the bucket's region, so this is the set of places
/// worth looking. Derived from the connections rather than configured separately, because it is the
/// same answer twice and one of the two would drift.
/// <para>
/// The Identity Center region is always included, even with no connection there. It is where the
/// first Access Grants instance is created and where a single-region deployment keeps everything,
/// so leaving it out would make the common case depend on a connection happening to name it.
/// </para>
/// </remarks>
public sealed class ConnectionGrantRegions(
    IConnectionStore connections,
    IOptionsMonitor<IdentityCenterSettings> options,
    ILogger<ConnectionGrantRegions> logger) : IAwsGrantRegions
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (options.CurrentValue.Region is { Length: > 0 } directoryRegion)
            regions.Add(directoryRegion);

        foreach (var connection in await connections.ListAsync(0, int.MaxValue, ct))
        {
            if (connection.Provider != ConnectionProvider.S3)
                continue;

            if (ReadRegion(connection.ConfigJson) is { Length: > 0 } region)
                regions.Add(region);
        }

        return [.. regions];
    }

    /// <summary>The region out of a connection's config, or null when it has none.</summary>
    /// <remarks>
    /// A connection whose config will not parse is skipped rather than throwing. It has a louder
    /// problem than this one, and failing here would take every other region's grants down with it
    /// — which denies searches over buckets that are configured perfectly well.
    /// </remarks>
    private string? ReadRegion(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(configJson);

            return document.RootElement.TryGetProperty("region", out var region)
                   && region.ValueKind == JsonValueKind.String
                ? region.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "A connection's configuration could not be read for its region");
            return null;
        }
    }
}
