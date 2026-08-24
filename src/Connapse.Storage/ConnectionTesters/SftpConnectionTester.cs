using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>
/// What the SFTP connection form hands to <see cref="SftpConnectionTester"/>. The private key
/// is passed rather than looked up, because the operator may be testing a key they have typed
/// but not yet saved.
/// </summary>
public record SftpConnectionTestSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "";
    public string AllowedRoot { get; init; } = "";
    public string PrivateKey { get; init; } = "";
    public string? Passphrase { get; init; }

    /// <summary>
    /// The fingerprint already pinned, if any, so a test reports a mismatch the same way a sync
    /// would rather than quietly succeeding against a different server.
    /// </summary>
    public string? PinnedHostKeyFingerprint { get; init; }
}

/// <summary>
/// Opens a session, verifies the host key, and confirms the allowed root resolves.
/// <para>
/// Deliberately does not walk the tree. A test button that lists a large remote turns a
/// configuration check into a minutes-long operation, and tells the operator nothing the root
/// check has not already told them: if the root resolves and stays inside itself, the
/// credential authenticated and the path exists.
/// </para>
/// </summary>
public class SftpConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not SftpConnectionTestSettings s)
            return new ConnectionTestResult { Success = false, Message = "Invalid settings type for the SFTP tester." };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));

        // No host key store: a test must never pin. Pinning is the sync's job, and doing it
        // here would record a fingerprint from a form the operator has not saved.
        using var connector = new SftpConnector(new SftpConnectorConfig
        {
            Host = s.Host,
            Port = s.Port,
            Username = s.Username,
            AllowedRoot = s.AllowedRoot,
            PinnedHostKeyFingerprint = s.PinnedHostKeyFingerprint,
            Credential = new SftpCredential { PrivateKey = s.PrivateKey, Passphrase = s.Passphrase },
        });

        try
        {
            string resolved = await connector.ProbeAsync(cts.Token);

            return new ConnectionTestResult
            {
                Success = true,
                Message = $"Connected to {s.Host}:{s.Port} as {s.Username}. "
                          + $"The allowed root resolved to {resolved} on the server."
            };
        }
        catch (SftpHostKeyMismatchException ex)
        {
            // Surfaced on its own, because it is the one failure retrying cannot fix and the
            // only one whose remedy is a decision rather than a correction.
            return new ConnectionTestResult { Success = false, Message = ex.Message };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"Timed out reaching {s.Host}:{s.Port}. Connapse has to be able to open a "
                          + "TCP connection to that address from where it runs — inside a container, "
                          + "the host machine is not 'localhost'."
            };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult { Success = false, Message = ex.Message };
        }
    }
}

