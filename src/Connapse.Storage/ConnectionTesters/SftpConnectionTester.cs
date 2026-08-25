using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using System.Net.Sockets;

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
    /// <summary>
    /// The name-resolution failure inside <paramref name="error"/>, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// Searched through the whole chain rather than checked on the outermost exception: SSH.NET
    /// wraps the socket failure, so the type that says what actually went wrong is never the one
    /// caught. Matched on the error code, not on the message, which is localised.
    /// </remarks>
    internal static SocketException? FindHostNotFound(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.HostNotFound } socket)
                return socket;
        }

        return null;
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not SftpConnectionTestSettings s)
            return new ConnectionTestResult { Success = false, Message = "Invalid settings type for the SFTP tester." };

        TimeSpan budget = timeout ?? TimeSpan.FromSeconds(15);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

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

            // The same budget the token carries, handed to the session itself. The token
            // covers the awaits; this covers the waits inside SSH.NET that the token cannot
            // reach. Without it a server that authenticates and then stalls its SFTP
            // subsystem holds this scope — and its Blazor circuit — well past the timeout
            // the operator was promised.
            OperationTimeout = budget,
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
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshOperationTimeoutException
                                      or TimeoutException)
        {
            // Reached the server, and it stopped answering partway through. Distinct from the
            // timeout above, which never got a connection at all — the remedies have nothing
            // in common, so saying "check that the address is reachable" here would send the
            // operator after a problem they do not have.
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"{s.Host}:{s.Port} accepted the connection but stopped responding. "
                          + "The SSH service is running but its SFTP subsystem is not answering."
            };
        }
        catch (Exception ex) when (FindHostNotFound(ex) is not null)
        {
            // Worth separating from every other connection failure, because it is the one whose
            // symptom contradicts the operator's own experience: the name works from their
            // desktop and not from here, so "no such host" reads as Connapse being wrong.
            //
            // It is not. A workstation appends a connection-specific DNS suffix before asking —
            // "server" is looked up as "server.example.lan" — and a container has no search
            // domain, so it looks up exactly what it was given. Docker stopped copying the
            // host's search domains into containers, so the difference is by design.
            return new ConnectionTestResult
            {
                Success = false,
                Message = $"'{s.Host}' did not resolve from inside Connapse. A short name often "
                          + "works on your own machine and not here: your computer appends a DNS "
                          + "suffix before looking it up, and a container does not. Use the "
                          + "address, or the fully-qualified name — the guided setup reports "
                          + "both — or set dns_search in docker-compose.yml."
            };
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult { Success = false, Message = ex.Message };
        }
    }
}

