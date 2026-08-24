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

        public string GetCanonicalPath(string path)
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
    public void CombineWithin_OrdinarySubPath_Resolves()
    {
        var server = new FakeServer();

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "docs/reports")
            .Should().Be("/srv/knowledge/docs/reports");
    }

    [Fact]
    public void CombineWithin_NoSubPath_ResolvesToTheRoot()
    {
        var server = new FakeServer();

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", null)
            .Should().Be("/srv/knowledge");
    }

    [Fact]
    public void CombineWithin_DotDotEscape_IsRefused()
    {
        var server = new FakeServer();

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "../../etc")
            .Should().BeNull();
    }

    [Fact]
    public void CombineWithin_DotDotThatStaysInside_IsAllowed()
    {
        var server = new FakeServer();

        // Refusing this would be over-strict: it resolves back inside the root.
        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "docs/../reports")
            .Should().Be("/srv/knowledge/reports");
    }

    [Fact]
    public void CombineWithin_AbsoluteSubPath_IsRefused()
    {
        var server = new FakeServer();

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "/etc/shadow")
            .Should().BeNull();
    }

    /// <summary>
    /// The case a lexical check passes and this exists to catch: the string never leaves the
    /// root, and the escape only appears once the server resolves the link.
    /// </summary>
    [Fact]
    public void CombineWithin_ServerSideSymlinkPointingOutside_IsRefused()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/escape"] = "/etc" });

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "escape")
            .Should().BeNull();
    }

    /// <summary>
    /// The subtle half: the leaf is an ordinary file, so asking it about links reveals
    /// nothing. Only its parent gives the escape away.
    /// </summary>
    [Fact]
    public void CombineWithin_SymlinkedAncestorWithOrdinaryLeaf_IsRefused()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/escape"] = "/etc" });

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "escape/shadow")
            .Should().BeNull();
    }

    /// <summary>
    /// A link inside the root that points back inside it is legitimate and must not be
    /// refused just for being a link — the check is about where a path lands, not how it got
    /// there.
    /// </summary>
    [Fact]
    public void CombineWithin_SymlinkPointingBackInsideTheRoot_IsAllowed()
    {
        var server = new FakeServer(new() { ["/srv/knowledge/shortcut"] = "/srv/knowledge/docs" });

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "shortcut/a.md")
            .Should().Be("/srv/knowledge/docs/a.md");
    }

    /// <summary>
    /// A root that is itself a symlink must not make contained paths look like escapes.
    /// Both sides are canonicalised for exactly this reason.
    /// </summary>
    [Fact]
    public void CombineWithin_SymlinkedRoot_StillConfinesAgainstTheResolvedRoot()
    {
        var server = new FakeServer(new() { ["/srv/knowledge"] = "/mnt/vol1/knowledge" });

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "docs")
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
    public void CombineWithin_ServerRefusesToResolve_IsRefused()
    {
        var server = new FakeServer();
        server.Unresolvable.Add("/srv/knowledge/docs");

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "docs")
            .Should().BeNull();
    }

    [Fact]
    public void CombineWithin_ServerRefusesToResolveTheRoot_IsRefused()
    {
        var server = new FakeServer();
        server.Unresolvable.Add("/srv/knowledge");

        SftpPathConfinement.CombineWithin(server, "/srv/knowledge", "docs")
            .Should().BeNull();
    }
}
