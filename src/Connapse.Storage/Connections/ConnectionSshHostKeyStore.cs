using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Connapse.Core.Utilities.LogSanitizer;

namespace Connapse.Storage.Connections;

/// <summary>
/// Pins an observed SSH host key onto its connection's <c>ConfigJson</c>.
/// </summary>
/// <remarks>
/// A singleton, because <see cref="Connapse.Storage.Connectors.ConnectorFactory"/> is one and
/// this is reached from a connector the factory built. <see cref="IConnectionStore"/> is
/// scoped, so a scope is opened per write — affordable, because this writes exactly once per
/// connection, on the first successful connect.
/// </remarks>
public class ConnectionSshHostKeyStore(
    IServiceScopeFactory scopeFactory,
    ILogger<ConnectionSshHostKeyStore> logger) : ISshHostKeyStore
{
    public const string FingerprintProperty = "hostKeyFingerprint";

    public async Task RecordFingerprintAsync(
        Guid connectionId, string fingerprint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var connection = await store.GetAsync(connectionId, ct);
        if (connection is null)
        {
            logger.LogWarning(
                "Cannot pin a host key: connection {ConnectionId} no longer exists", connectionId);
            return;
        }

        var config = ParseObject(connection.ConfigJson);
        if (config is null)
        {
            logger.LogWarning(
                "Cannot pin a host key: the configuration for connection {ConnectionId} is not a JSON object",
                connectionId);
            return;
        }

        // A fingerprint already present is left alone. The mismatch path refuses the
        // connection rather than reaching here, so an arriving write against an existing
        // value means two sync cycles raced — and the recorded value is the one every other
        // cycle has been comparing against, so it wins.
        if (config[FingerprintProperty] is JsonValue existing
            && !string.IsNullOrWhiteSpace(existing.ToString()))
        {
            return;
        }

        config[FingerprintProperty] = fingerprint;

        // Name is passed through unchanged rather than left null: UpdateConnectionRequest
        // treats null as "leave alone" for the name too, but being explicit keeps this from
        // depending on that.
        await store.UpdateAsync(
            connectionId,
            new UpdateConnectionRequest(connection.Name, config.ToJsonString(), Secret: null),
            ct);

        logger.LogInformation(
            "Pinned SSH host key {Fingerprint} to connection {ConnectionName} ({ConnectionId}) on first use",
            Sanitize(fingerprint), Sanitize(connection.Name), connectionId);
    }

    /// <summary>
    /// The connection's configuration as a mutable object, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than a fresh empty object on purpose. Writing the fingerprint into a new
    /// object would replace the host, port, username and allowed root with nothing, breaking
    /// the connection outright to record a value that only matters while it works. Not
    /// reachable in practice — the factory parses the same configuration to build the
    /// connector at all — but the failure it would cause is bad enough to be explicit about.
    /// </remarks>
    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
