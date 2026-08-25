using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Storage.Connectors;

/// <summary>
/// The two secrets an SFTP connection needs, stored as one JSON object inside the single
/// encrypted <c>SecretProtected</c> column.
/// <para>
/// An encrypted private key needs a passphrase to open it, which makes two secrets where the
/// connection schema has one field. Packing them into the existing protected blob means one
/// value to encrypt, one to decrypt, and no migration — where a second column would need
/// both, plus a second thing to remember to protect.
/// </para>
/// </summary>
public record SftpCredential
{
    [JsonPropertyName("privateKey")]
    public string PrivateKey { get; init; } = "";

    /// <summary>Null or blank when the key is not encrypted.</summary>
    [JsonPropertyName("passphrase")]
    public string? Passphrase { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads a credential out of a connection's decrypted secret. Returns null when there is
    /// nothing stored, or when the blob is not the expected shape — the caller turns that
    /// into a connect-time failure naming the connection, which is more use than a JSON
    /// parse error surfacing from inside the sync loop.
    /// </summary>
    public static SftpCredential? TryParse(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<SftpCredential>(secret, Options);

            return string.IsNullOrWhiteSpace(parsed?.PrivateKey) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serialises a credential for storage. Used by the connection form rather than the
    /// connector, but lives here so the shape is defined once.
    /// </summary>
    public string ToSecretJson() => JsonSerializer.Serialize(this, Options);
}

/// <summary>
/// Everything an <see cref="SftpConnector"/> needs: where the server is, who to be, what the
/// connection is bounded to, and what the source picked inside that.
/// </summary>
public record SftpConnectorConfig
{
    public string Host { get; init; } = "";

    public int Port { get; init; } = 22;

    public string Username { get; init; } = "";

    /// <summary>
    /// The connection's boundary, as an absolute remote path. Every path this connector
    /// touches is confined beneath it, resolved on the server.
    /// </summary>
    public string AllowedRoot { get; init; } = "";

    /// <summary>The source's scope within the root. Null or blank means the root itself.</summary>
    public string? SubPath { get; init; }

    public IReadOnlyList<string> IncludePatterns { get; init; } = [];

    public IReadOnlyList<string> ExcludePatterns { get; init; } = [];

    /// <summary>
    /// The fingerprint this connection is pinned to, or null when nothing has been recorded
    /// and the next successful connect should record one.
    /// </summary>
    public string? PinnedHostKeyFingerprint { get; init; }

    public SftpCredential? Credential { get; init; }

    /// <summary>
    /// Which connection to pin a newly-observed fingerprint against. Empty when the connector
    /// was built outside a stored connection, as the connection tester does, in which case
    /// nothing is recorded.
    /// </summary>
    public Guid ConnectionId { get; init; }

    /// <summary>
    /// How long a single SFTP request may take before the session gives up, and how long the
    /// initial handshake may take.
    /// </summary>
    /// <remarks>
    /// A backstop, not the primary mechanism — cancellation tokens are. It covers the case a
    /// token cannot: SSH.NET waits on its own semaphores inside a request, and a server that
    /// answers the handshake and then stops answering leaves the caller holding an SSH session
    /// and a socket with nothing to release them. The default is infinite, which is the wrong
    /// default for a server nobody here controls.
    /// </remarks>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
