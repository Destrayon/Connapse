using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The SFTP connector against a real OpenSSH server.
/// <para>
/// Every confinement test here is one a lexical prefix check would pass. That is deliberate:
/// the connector's whole reason for having its own path handling, rather than reusing
/// <c>PathConfinement</c>, is that the local helper degrades to exactly such a check when it
/// is handed a remote path — and degrades silently.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class SftpConnectorIntegrationTests : IAsyncLifetime
{
    private readonly SftpServerFixture _server = new();

    /// <summary>The connection's boundary, as an SFTP client sees it.</summary>
    private const string AllowedRoot = "/data";

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();

        await _server.CreateDirectoryAsync("/data/docs");
        await _server.WriteFileAsync("/data/a.md", "alpha");
        await _server.WriteFileAsync("/data/docs/b.md", "bravo");
        await _server.WriteFileAsync("/data/docs/skip.tmp", "temporary");

        // Outside the allowed root, inside the account's chroot — what an escape reaches.
        await _server.CreateDirectoryAsync("/secret");
        await _server.WriteFileAsync("/secret/keys.txt", "do not index me");
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private SftpConnector Connect(
        string? subPath = null,
        string? pinned = null,
        ISshHostKeyStore? hostKeyStore = null,
        IReadOnlyList<string>? include = null,
        IReadOnlyList<string>? exclude = null,
        Guid connectionId = default) =>
        new(new SftpConnectorConfig
        {
            Host = _server.Host,
            Port = _server.Port,
            Username = SftpServerFixture.Username,
            AllowedRoot = AllowedRoot,
            SubPath = subPath,
            PinnedHostKeyFingerprint = pinned,
            IncludePatterns = include ?? [],
            ExcludePatterns = exclude ?? [],
            Credential = new SftpCredential { PrivateKey = _server.PrivateKeyPem },
            ConnectionId = connectionId,
        }, hostKeyStore);

    // ── Listing and reading ────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_WalksTheTreeBeneathTheRoot()
    {
        using var connector = Connect();

        var files = await connector.ListFilesAsync();

        files.Select(f => f.Path).Should().BeEquivalentTo(
            "/data/a.md", "/data/docs/b.md", "/data/docs/skip.tmp");
    }

    [Fact]
    public async Task ListFiles_ReportsSizeAndModifiedTime()
    {
        using var connector = Connect();

        var file = (await connector.ListFilesAsync()).Single(f => f.Path == "/data/a.md");

        file.SizeBytes.Should().Be(5);
        file.LastModified.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task ListFiles_HonoursIncludeAndExcludePatterns()
    {
        using var connector = Connect(exclude: ["*.tmp"]);

        (await connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/data/a.md", "/data/docs/b.md");
    }

    [Fact]
    public async Task ListFiles_WithASubPath_ScopesToIt()
    {
        using var connector = Connect(subPath: "docs");

        (await connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/data/docs/b.md", "/data/docs/skip.tmp");
    }

    [Fact]
    public async Task ReadFile_ReturnsTheContent()
    {
        using var connector = Connect();

        await using var stream = await connector.ReadFileAsync("/data/docs/b.md");
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("bravo");
    }

    [Fact]
    public async Task Exists_IsTrueForAFileInsideTheRootAndFalseOutside()
    {
        using var connector = Connect();

        (await connector.ExistsAsync("/data/a.md")).Should().BeTrue();
        (await connector.ExistsAsync("/secret/keys.txt")).Should().BeFalse();
    }

    // ── Confinement ────────────────────────────────────────────────────────

    [Fact]
    public async Task SubPathContainingDotDot_IsRefused()
    {
        using var connector = Connect(subPath: "../secret");

        Func<Task> act = () => connector.ListFilesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The case that justifies resolving on the server. The string
    /// <c>/data/escape</c> never leaves the root, and only the server knows it is a link.
    /// </summary>
    [Fact]
    public async Task SubPathThroughAServerSideSymlink_IsRefused()
    {
        await _server.CreateSymlinkAsync("/data/escape", "/secret");

        using var connector = Connect(subPath: "escape");

        Func<Task> act = () => connector.ListFilesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// A link inside the tree is stepped over rather than followed, so its target never
    /// reaches the index — even when the link itself is inside the root.
    /// </summary>
    [Fact]
    public async Task ListFiles_DoesNotFollowSymlinksOutOfTheRoot()
    {
        await _server.CreateSymlinkAsync("/data/escape", "/secret");

        using var connector = Connect();

        var paths = (await connector.ListFilesAsync()).Select(f => f.Path).ToList();

        paths.Should().NotContain(p => p.Contains("keys.txt"),
            "following the link would index a file outside the connection's allowed root");
        paths.Should().Contain("/data/a.md", "the ordinary tree must still be walked");
    }

    [Fact]
    public async Task ReadFile_OutsideTheRoot_IsRefused()
    {
        using var connector = Connect();

        Func<Task> act = () => connector.ReadFileAsync("/secret/keys.txt");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ReadFile_TraversingOutOfTheRoot_IsRefused()
    {
        using var connector = Connect();

        Func<Task> act = () => connector.ReadFileAsync("/data/../secret/keys.txt");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── Listing completeness ───────────────────────────────────────────────

    /// <summary>
    /// The failure mode the delete guard exists to bound, prevented one level lower. A
    /// listing that quietly omits an unreadable subtree is indistinguishable, to the
    /// reconcile, from every file under it having been deleted.
    /// </summary>
    [Fact]
    public async Task ListFiles_UnreadableDirectory_FailsRatherThanReturningFewerFiles()
    {
        await _server.MakeUnreadableAsync("/data/docs");

        using var connector = Connect();

        Func<Task> act = () => connector.ListFilesAsync();

        (await act.Should().ThrowAsync<IOException>())
            .WithMessage("*partial listing*");
    }

    // ── Host key pinning ───────────────────────────────────────────────────

    /// <summary>Records what was pinned, so a test can assert on it.</summary>
    private sealed class RecordingHostKeyStore : ISshHostKeyStore
    {
        public List<(Guid ConnectionId, string Fingerprint)> Recorded { get; } = [];

        public Task RecordFingerprintAsync(Guid connectionId, string fingerprint, CancellationToken ct = default)
        {
            Recorded.Add((connectionId, fingerprint));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FirstConnect_RecordsTheHostKeyFingerprint()
    {
        var store = new RecordingHostKeyStore();
        var connectionId = Guid.NewGuid();

        using var connector = Connect(hostKeyStore: store, connectionId: connectionId);
        await connector.ListFilesAsync();

        store.Recorded.Should().ContainSingle()
            .Which.Should().Match<(Guid Id, string Fingerprint)>(
                r => r.Id == connectionId && r.Fingerprint.StartsWith("SHA256:"));
    }

    /// <summary>
    /// Pinning is first-use only. Re-recording on every cycle would make the stored value
    /// track whatever answered most recently, which is the opposite of pinning.
    /// </summary>
    [Fact]
    public async Task ConnectWithAMatchingPin_RecordsNothingFurther()
    {
        string fingerprint = await ObserveFingerprintAsync();
        var store = new RecordingHostKeyStore();

        using var connector = Connect(pinned: fingerprint, hostKeyStore: store, connectionId: Guid.NewGuid());
        await connector.ListFilesAsync();

        store.Recorded.Should().BeEmpty();
    }

    /// <summary>
    /// Asserted on the specific exception and its message, not just "something threw". An
    /// unreachable server also throws, so a loose assertion here would pass whether or not
    /// the host key was ever checked.
    /// </summary>
    [Fact]
    public async Task ConnectWithAMismatchedPin_IsRefused()
    {
        using var connector = Connect(pinned: "SHA256:definitelyNotTheServersKey");

        Func<Task> act = () => connector.ListFilesAsync();

        (await act.Should().ThrowAsync<SftpHostKeyMismatchException>())
            .WithMessage("*SHA256:definitelyNotTheServersKey*",
                "the operator has to see which key was expected")
            .And.Message.Should().Contain("clear the recorded fingerprint",
                "and what to do if the rekey was theirs");
    }

    /// <summary>
    /// The realistic attack, and the one trust-on-first-use is chosen to catch: the address
    /// worked yesterday and something else answers today.
    /// </summary>
    [Fact]
    public async Task AfterTheServerRekeys_APinnedConnectionIsRefused()
    {
        string original = await ObserveFingerprintAsync();

        await _server.RegenerateHostKeysAsync();

        using var connector = Connect(pinned: original);

        Func<Task> act = () => connector.ListFilesAsync();

        (await act.Should().ThrowAsync<SftpHostKeyMismatchException>())
            .WithMessage($"*{original}*", "the fingerprint that was trusted must be named");
    }

    /// <summary>
    /// Clearing the recorded fingerprint is the documented way to accept a rekey, so the same
    /// server must connect again once nothing is pinned.
    /// </summary>
    [Fact]
    public async Task AfterTheServerRekeys_ClearingThePinLetsItConnectAgain()
    {
        await _server.RegenerateHostKeysAsync();

        using var connector = Connect(pinned: null);

        (await connector.ListFilesAsync()).Should().NotBeEmpty();
    }

    private async Task<string> ObserveFingerprintAsync()
    {
        var store = new RecordingHostKeyStore();

        using var connector = Connect(hostKeyStore: store, connectionId: Guid.NewGuid());
        await connector.ListFilesAsync();

        return store.Recorded.Single().Fingerprint;
    }
}

