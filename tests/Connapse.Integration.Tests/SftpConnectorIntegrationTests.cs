using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
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
    public async Task ListFiles_HonoursExcludePatterns()
    {
        using var connector = Connect(exclude: ["*.tmp"]);

        (await connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/data/a.md", "/data/docs/b.md");
    }

    /// <summary>
    /// Split out because the combined test only ever passed an exclude pattern, so include
    /// filtering was named but never exercised — and an include list that matched nothing would
    /// have emptied a source silently.
    /// </summary>
    [Fact]
    public async Task ListFiles_HonoursIncludePatterns()
    {
        using var connector = Connect(include: ["*.md"]);

        (await connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/data/a.md", "/data/docs/b.md");
    }

    [Fact]
    public async Task ListFiles_IncludeAndExcludeTogether_ApplyBoth()
    {
        await _server.WriteFileAsync("/data/draft.md", "draft");

        using var connector = Connect(include: ["*.md"], exclude: ["draft.*"]);

        (await connector.ListFilesAsync()).Select(f => f.Path)
            .Should().BeEquivalentTo("/data/a.md", "/data/docs/b.md");
    }

    /// <summary>
    /// A pattern crafted to backtrack catastrophically. Compiled with a match timeout, so it
    /// gives up instead of pinning the sync thread for this source on every cycle for ever.
    /// Patterns come from a source's scope, so one bad entry is replayed indefinitely.
    /// </summary>
    [Fact]
    public async Task ListFiles_PathologicalGlob_DoesNotHangTheSync()
    {
        await _server.WriteFileAsync("/data/" + new string('a', 90) + ".md", "x");

        using var connector = Connect(exclude: ["*a*a*a*a*a*a*a*a*a*a*b"]);

        var listing = connector.ListFilesAsync();

        (await listing.WaitAsync(TimeSpan.FromSeconds(60))).Should().NotBeEmpty(
            "the timeout must bound the match rather than the sync waiting on it");
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

    /// <summary>
    /// The leaf itself being a link, rather than an ancestor. Confinement canonicalises the
    /// parent and reattaches the name, so the parent check passes and the name is not a
    /// traversal — nothing so far has looked at what the leaf actually is.
    /// </summary>
    /// <remarks>
    /// The walk never lists a symlink, so this is only reachable when a file that was listed as
    /// regular is swapped for a link before it is read. Narrow, but it is the same shape as the
    /// escape the ancestor check exists to stop.
    /// </remarks>
    [Fact]
    public async Task ReadFile_LeafIsASymlinkPointingOutsideTheRoot_IsRefused()
    {
        await _server.CreateSymlinkAsync("/data/looks-ordinary.md", "/secret/keys.txt");

        using var connector = Connect();

        Func<Task> act = () => connector.ReadFileAsync("/data/looks-ordinary.md");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    /// <summary>
    /// Refused even when the target is inside the root, and the consistency is the point: the
    /// walk steps over links, so no path the ingestion queue holds is ever one. Resolving them
    /// here would make the read path follow what the listing deliberately does not — two
    /// answers to the same question, which is exactly how #365 happened locally.
    /// </summary>
    [Fact]
    public async Task ReadFile_LeafIsASymlinkPointingInsideTheRoot_IsAlsoRefused()
    {
        await _server.CreateSymlinkAsync("/data/alias.md", "/data/a.md");

        using var connector = Connect();

        Func<Task> act = () => connector.ReadFileAsync("/data/alias.md");

        (await act.Should().ThrowAsync<UnauthorizedAccessException>())
            .WithMessage("*symbolic link*");
    }

    /// <summary>
    /// And the ordinary case still works, so the check above cannot have made every read fail.
    /// </summary>
    [Fact]
    public async Task ReadFile_OrdinaryFile_IsUnaffectedByTheLinkCheck()
    {
        using var connector = Connect();

        await using var stream = await connector.ReadFileAsync("/data/a.md");
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("alpha");
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

    // ── Generated key pairs ────────────────────────────────────────────────

    /// <summary>
    /// The test that makes <see cref="SshKeyPairGenerator"/> trustworthy.
    /// </summary>
    /// <remarks>
    /// A wrong <c>authorized_keys</c> encoding produces a line that looks entirely correct
    /// and simply fails to authenticate, with nothing anywhere pointing at the cause. Unit
    /// tests can check the blob's structure but cannot tell you whether a real OpenSSH server
    /// accepts it. This installs a generated public key the way the setup command will, and
    /// connects with the matching private half.
    /// </remarks>
    [Fact]
    public async Task GeneratedKeyPair_AuthenticatesAgainstARealServer()
    {
        var pair = SshKeyPairGenerator.Generate("connapse-generated");
        await _server.AuthorizeKeyAsync(pair.PublicKeyLine);

        using var connector = new SftpConnector(new SftpConnectorConfig
        {
            Host = _server.Host,
            Port = _server.Port,
            Username = SftpServerFixture.Username,
            AllowedRoot = AllowedRoot,
            Credential = new SftpCredential { PrivateKey = pair.PrivateKeyPem },
        });

        (await connector.ListFilesAsync()).Should().NotBeEmpty(
            "a generated pair must authenticate, or the setup flow hands operators a key that "
            + "silently does not work");
    }

    /// <summary>
    /// The negative half. Without it the test above could pass because the server accepts
    /// anything — the fixture's own key is already authorized, so a generated key that was
    /// never really used would look identical.
    /// </summary>
    [Fact]
    public async Task GeneratedKeyPair_NotInstalledOnTheServer_IsRefused()
    {
        var pair = SshKeyPairGenerator.Generate("never-installed");

        using var connector = new SftpConnector(new SftpConnectorConfig
        {
            Host = _server.Host,
            Port = _server.Port,
            Username = SftpServerFixture.Username,
            AllowedRoot = AllowedRoot,
            Credential = new SftpCredential { PrivateKey = pair.PrivateKeyPem },
        });

        Func<Task> act = () => connector.ListFilesAsync();

        await act.Should().ThrowAsync<Exception>();
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


