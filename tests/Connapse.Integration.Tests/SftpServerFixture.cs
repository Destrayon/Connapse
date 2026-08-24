using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Connapse.Integration.Tests;

/// <summary>
/// A real OpenSSH server in a container, so the SFTP connector is exercised against the
/// protocol rather than a mock.
/// <para>
/// This is what makes the confinement tests worth anything. The escape that matters is a
/// symlink resolved <b>server-side</b>, and no fake can tell you whether the code asked the
/// server to resolve it or resolved it locally against the machine running the tests — which
/// is the bug the whole design is arranged to avoid.
/// </para>
/// <para>
/// The key pair is generated per run rather than committed. A private key in the repository
/// is a private key in the repository, however loudly the file name says "test".
/// </para>
/// </summary>
public sealed class SftpServerFixture : IAsyncLifetime
{
    /// <summary>
    /// The account inside the container. Its home is the chroot, so everything below appears
    /// to an SFTP client as if it were at the filesystem root.
    /// </summary>
    public const string Username = "tester";

    private const string Home = $"/home/{Username}";

    private IContainer _container = null!;

    /// <summary>PKCS#1 PEM, the form the connection form will accept from an operator.</summary>
    public string PrivateKeyPem { get; private set; } = null!;

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(22);

    public async Task InitializeAsync()
    {
        using var rsa = RSA.Create(3072);
        PrivateKeyPem = rsa.ExportRSAPrivateKeyPem();

        _container = new ContainerBuilder()
            .WithImage("atmoz/sftp:alpine")
            .WithPortBinding(22, assignRandomHostPort: true)

            // No password field at all: the account exists for key authentication only, which
            // is what the connector does.
            .WithCommand($"{Username}::1001")

            // atmoz/sftp folds every *.pub under .ssh/keys into authorized_keys on start.
            .WithResourceMapping(
                Encoding.ASCII.GetBytes(ToOpenSshPublicKey(rsa)),
                $"{Home}/.ssh/keys/generated.pub")

            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(22))
            .Build();

        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    // ── Seeding ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a shell command as root inside the container. Paths here are real container
    /// paths; an SFTP client sees them with <see cref="Home"/> stripped off the front.
    /// </summary>
    public async Task<string> ExecAsync(string command)
    {
        var result = await _container.ExecAsync(["sh", "-c", command]);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Setup command failed ({result.ExitCode}): {command}\n{result.Stderr}");

        return result.Stdout;
    }

    /// <summary>Creates a directory the SFTP account can read, named as the client sees it.</summary>
    public Task CreateDirectoryAsync(string sftpPath) =>
        ExecAsync($"mkdir -p '{Home}{sftpPath}' && chown -R {Username} '{Home}{sftpPath}'");

    /// <summary>Writes a file, named as the client sees it.</summary>
    public Task WriteFileAsync(string sftpPath, string content) =>
        ExecAsync(
            $"mkdir -p \"$(dirname '{Home}{sftpPath}')\" && "
            + $"printf '%s' '{content}' > '{Home}{sftpPath}' && "
            + $"chown {Username} '{Home}{sftpPath}'");

    /// <summary>
    /// Creates a symlink. <paramref name="target"/> is interpreted inside the chroot, so an
    /// absolute target like <c>/secret</c> resolves to the account's own tree — which is the
    /// escape being tested: outside the connection's allowed root, still inside the chroot.
    /// </summary>
    public Task CreateSymlinkAsync(string sftpPath, string target) =>
        ExecAsync($"ln -s '{target}' '{Home}{sftpPath}' && chown -h {Username} '{Home}{sftpPath}'");

    /// <summary>Makes a directory unreadable by the SFTP account.</summary>
    public Task MakeUnreadableAsync(string sftpPath) =>
        ExecAsync($"chown root '{Home}{sftpPath}' && chmod 700 '{Home}{sftpPath}'");

    /// <summary>
    /// Appends a public key to the account's <c>authorized_keys</c>, the way the generated
    /// setup command does on a real host. Lets a test prove a Connapse-generated key pair
    /// actually authenticates.
    /// </summary>
    public Task AuthorizeKeyAsync(string publicKeyLine) =>
        ExecAsync(
            $"mkdir -p '{Home}/.ssh' && "
            + $"printf '%s\\n' '{publicKeyLine}' >> '{Home}/.ssh/authorized_keys' && "
            + $"chown -R {Username} '{Home}/.ssh' && "
            + $"chmod 700 '{Home}/.ssh' && chmod 600 '{Home}/.ssh/authorized_keys'");

    /// <summary>
    /// Reads the server's host key fingerprint the way the generated setup command does —
    /// with <c>ssh-keygen -l</c>, on the machine itself, never over the network.
    /// </summary>
    /// <remarks>
    /// This is the out-of-band channel that makes verified-first-use possible, so a test using
    /// it is testing the real mechanism rather than a stand-in.
    /// </remarks>
    public async Task<string> ReadHostKeyFingerprintAsync()
    {
        string output = await ExecAsync(
            "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null "
            + "|| ssh-keygen -lf /etc/ssh/ssh_host_rsa_key.pub");

        return output.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Trim();
    }

    /// <summary>
    /// Replaces the server's host keys and restarts sshd, so the next connection presents a
    /// different key. This is what a rekey looks like to a client, and what an interposition
    /// looks like too — the connector cannot tell them apart, which is the point of pinning.
    /// </summary>
    public async Task RegenerateHostKeysAsync()
    {
        await ExecAsync(
            "rm -f /etc/ssh/ssh_host_*_key* && "
            + "ssh-keygen -A && "
            + "kill -HUP 1 2>/dev/null; pkill -HUP sshd 2>/dev/null; true");

        // sshd re-reads its keys on the next accept, but the restart is not instantaneous and
        // there is no readiness signal for "now serving the new key".
        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    // ── Key encoding ───────────────────────────────────────────────────────

    /// <summary>
    /// Encodes an RSA public key in the format <c>authorized_keys</c> expects: the algorithm
    /// name, exponent and modulus, each length-prefixed, then base64.
    /// </summary>
    private static string ToOpenSshPublicKey(RSA rsa)
    {
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);

        using var buffer = new MemoryStream();
        WriteLengthPrefixed(buffer, "ssh-rsa"u8.ToArray());
        WriteLengthPrefixed(buffer, ToMpint(p.Exponent!));
        WriteLengthPrefixed(buffer, ToMpint(p.Modulus!));

        return $"ssh-rsa {Convert.ToBase64String(buffer.ToArray())} connapse-integration-test\n";
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    /// <summary>
    /// SSH multiple-precision integers are signed, so a value whose top bit is set needs a
    /// leading zero byte or it reads as negative.
    /// </summary>
    private static byte[] ToMpint(byte[] value) =>
        value.Length > 0 && value[0] >= 0x80 ? [0, .. value] : value;
}
