using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// The resolver stands in for the remote server's <c>SSH_FXP_REALPATH</c>. It is what makes
/// these tests meaningful: the escapes below are ones a purely lexical check would pass, and
/// they are only caught because resolution happens where the symlinks actually live.
/// </summary>
[Trait("Category", "Unit")]
public class SftpPathConfinementTests
{
    /// <summary>
    /// A server whose symlink table is supplied by the test. Anything not named resolves by
    /// collapsing "." and ".." segments, the way a real server would.
    /// </summary>
    private sealed class FakeServer(Dictionary<string, string>? links = null) : ISftpRealPathResolver
    {
        private readonly Dictionary<string, string> _links = links ?? [];

        public HashSet<string> Unresolvable { get; } = [];

        public Task<string> GetCanonicalPathAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(Resolve(path));

        private string Resolve(string path)
        {
            if (Unresolvable.Contains(path))
                throw new InvalidOperationException($"The server refused to resolve '{path}'.");

            var stack = new List<string>();

            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;

                if (segment == "..")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                stack.Add(segment);

                // A link is followed the moment the walk reaches it, which is what makes an
                // ancestor link an escape even when the leaf itself is an ordinary file.
                string soFar = "/" + string.Join('/', stack);
                if (_links.TryGetValue(soFar, out string? target))
                {
                    stack.Clear();
                    stack.AddRange(target.Split('/', StringSplitOptions.RemoveEmptyEntries));
                }
            }

            return "/" + string.Join('/', stack);
        }
    }

    [Fact]
    public async Task CombineWithin_OrdinarySubPath_Resolves()
    {
        var server = new FakeServer();

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "docs/reports"))
            .Should().Be("/srv/knowledge/docs/reports");
    }

    [Fact]
    public async Task CombineWithin_NoSubPath_ResolvesToTheRoot()
    {
        var server = new FakeServer();

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", null))
            .Should().Be("/srv/knowledge");
    }

    [Fact]
    public async Task CombineWithin_DotDotEscape_IsRefused()
    {
        var server = new FakeServer();

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "../../etc"))
            .Should().BeNull();
    }

    [Fact]
    public async Task CombineWithin_DotDotThatStaysInside_IsAllowed()
    {
        var server = new FakeServer();

        // Refusing this would be over-strict: it resolves back inside the root.
        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "docs/../reports"))
            .Should().Be("/srv/knowledge/reports");
    }

    [Fact]
    public async Task CombineWithin_AbsoluteSubPath_IsRefused()
    {
        var server = new FakeServer();

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "/etc/shadow"))
            .Should().BeNull();
    }

    /// <summary>
    /// The case a lexical check passes and this exists to catch: the string never leaves the
    /// root, and the escape only appears once the server resolves the link.
    /// </summary>
    [Fact]
    public async Task CombineWithin_ServerSideSymlinkPointingOutside_IsRefused()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/escape"] = "/etc" });

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "escape"))
            .Should().BeNull();
    }

    /// <summary>
    /// The subtle half: the leaf is an ordinary file, so asking it about links reveals
    /// nothing. Only its parent gives the escape away.
    /// </summary>
    [Fact]
    public async Task CombineWithin_SymlinkedAncestorWithOrdinaryLeaf_IsRefused()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/escape"] = "/etc" });

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "escape/shadow"))
            .Should().BeNull();
    }

    /// <summary>
    /// A link inside the root that points back inside it is legitimate and must not be
    /// refused just for being a link — the check is about where a path lands, not how it got
    /// there.
    /// </summary>
    [Fact]
    public async Task CombineWithin_SymlinkPointingBackInsideTheRoot_IsAllowed()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/shortcut"] = "/srv/knowledge/docs" });

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "shortcut/a.md"))
            .Should().Be("/srv/knowledge/docs/a.md");
    }

    /// <summary>
    /// A root that is itself a symlink must not make contained paths look like escapes.
    /// Both sides are canonicalised for exactly this reason.
    /// </summary>
    [Fact]
    public async Task CombineWithin_SymlinkedRoot_StillConfinesAgainstTheResolvedRoot()
    {
        var server = new FakeServer(new() { ["/srv/knowledge"] = "/mnt/vol1/knowledge" });

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "docs"))
            .Should().Be("/mnt/vol1/knowledge/docs");
    }

    /// <summary>
    /// The nginx `alias` off-by-slash bug: a sibling sharing a name prefix is not inside.
    /// </summary>
    [Fact]
    public void IsWithin_SiblingSharingANamePrefix_IsOutside()
    {
        SftpPathConfinement.IsWithin("/data", "/data-other/secrets").Should().BeFalse();
    }

    [Fact]
    public void IsWithin_TheRootItself_IsInside()
    {
        SftpPathConfinement.IsWithin("/data", "/data").Should().BeTrue();
    }

    [Fact]
    public void IsWithin_TrailingSeparatorOnTheRoot_DoesNotChangeTheAnswer()
    {
        SftpPathConfinement.IsWithin("/data/", "/data/x").Should().BeTrue();
        SftpPathConfinement.IsWithin("/data/", "/data").Should().BeTrue();
    }

    /// <summary>
    /// Case is compared ordinally even though a Windows OpenSSH server is case-insensitive
    /// underneath. Refusing a legitimate path is visible and fixable; admitting an escape is
    /// silent, so this deliberately takes the visible failure.
    /// </summary>
    [Fact]
    public void IsWithin_DifferingCase_IsOutside()
    {
        SftpPathConfinement.IsWithin("/C:/Users/me", "/c:/users/me/docs").Should().BeFalse();
    }

    [Fact]
    public void IsWithin_WindowsStyleRoot_ConfinesNormally()
    {
        SftpPathConfinement.IsWithin("/C:/Users/me/docs", "/C:/Users/me/docs/a.md").Should().BeTrue();
        SftpPathConfinement.IsWithin("/C:/Users/me/docs", "/C:/Users/me/private").Should().BeFalse();
    }

    /// <summary>
    /// A path the server will not resolve is one we cannot vouch for, so it is refused rather
    /// than compared as an unverified string.
    /// </summary>
    [Fact]
    public async Task CombineWithin_ServerRefusesToResolve_IsRefused()
    {
        var server = new FakeServer();
        server.Unresolvable.Add("/srv/knowledge/docs");

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "docs"))
            .Should().BeNull();
    }

    [Fact]
    public async Task CombineWithin_ServerRefusesToResolveTheRoot_IsRefused()
    {
        var server = new FakeServer();
        server.Unresolvable.Add("/srv/knowledge");

        (await SftpPathConfinement.CombineWithinAsync(server, "/srv/knowledge", "docs"))
            .Should().BeNull();
    }

    /// <summary>
    /// A server that answered the handshake and then went quiet. Real ones do this: sshd is up
    /// and authenticating, its SFTP subsystem is wedged or the box is thrashing.
    /// </summary>
    private sealed class StallingServer : ISftpRealPathResolver
    {
        public async Task<string> GetCanonicalPathAsync(string path, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return path;
        }
    }

    [Fact]
    public async Task ResolveWithin_ServerStopsAnswering_ObservesCancellation()
    {
        // The reason this seam is asynchronous. Canonicalisation used to go through SSH.NET's
        // blocking ChangeDirectory, which no token can interrupt — so a connection test's
        // 15-second budget covered the handshake and then waited indefinitely on the very
        // request that stalls, holding its Blazor circuit and SSH session open.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> act = async () => await SftpPathConfinement.ResolveWithinAsync(
            new StallingServer(), "/srv/knowledge", "/srv/knowledge/docs", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation must reach the round-trip, not just the awaits around it");
    }

    [Fact]
    public void SftpConnectorConfig_OperationTimeout_IsFinite()
    {
        // SSH.NET's own default is infinite. A session that inherits that has no way back from
        // a server that stops answering: nothing releases the SSH session or its socket, and
        // SourceSyncService builds a connector every cycle.
        var config = new Connapse.Storage.Connectors.SftpConnectorConfig();

        config.OperationTimeout.Should().BePositive().And.NotBe(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task ResolveWithin_Cancellation_IsNotSwallowedAsAnUnresolvablePath()
    {
        // The catch that refuses paths the server will not resolve must not also swallow a
        // cancelled one: returning null there would report "outside the allowed root" for a
        // path nobody ever asked about, and the caller would never learn it timed out.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = async () => await SftpPathConfinement.CombineWithinAsync(
            new StallingServer(), "/srv/knowledge", "docs", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
