using System.Runtime.InteropServices;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Confinement of a source's subpath beneath its connection's allowed root (#365).
/// <para>
/// The bug these pin: the original check was <c>Path.GetFullPath</c> plus a
/// <c>StartsWith</c>, which is purely lexical. A junction inside the root passed it, and the
/// connector's recursive walk then read straight through to the target — verified against
/// .NET before the fix.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class PathConfinementTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), $"connapse-confine-{Guid.NewGuid():N}");

    private readonly string _root;
    private readonly string _outside;

    public PathConfinementTests()
    {
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "keyring.txt"), "SECRET");
    }

    public void Dispose()
    {
        // Remove any directory links before deleting the tree, so cleanup cannot follow one
        // out of the temp directory and delete the target.
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.GetDirectories(_root))
            {
                if (new DirectoryInfo(dir).LinkTarget is not null ||
                    new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(dir);
                }
            }
        }

        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// Creates a directory link, returning false when the platform will not allow it without
    /// elevation. Windows permits junctions unprivileged but not symlinks; Unix permits
    /// symlinks.
    /// </summary>
    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc!.WaitForExit(10_000);
                return proc.ExitCode == 0 && Directory.Exists(linkPath);
            }

            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void CombineWithin_OrdinarySubPath_IsAllowed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "team"));

        string? result = PathConfinement.CombineWithin(_root, "team");

        result.Should().NotBeNull();
        result.Should().StartWith(Path.GetFullPath(_root));
    }

    [Fact]
    public void CombineWithin_DotDotEscape_IsRejected()
    {
        PathConfinement.CombineWithin(_root, "../outside").Should().BeNull();
    }

    [Fact]
    public void CombineWithin_AbsolutePath_IsRejected()
    {
        // Path.Combine discards its first argument when the second is rooted, so honouring an
        // absolute "relative" path would hand back somewhere else on disk entirely. The
        // connector's own GetFullPath had exactly this hole, returning early for rooted paths.
        PathConfinement.CombineWithin(_root, _outside).Should().BeNull();
    }

    [Fact]
    public void IsWithin_SiblingSharingANamePrefix_IsRejected()
    {
        // "/data-other" starts with "/data" as a string but is not inside it. This is the
        // nginx `alias` off-by-slash bug.
        string root = Path.Combine(_base, "data");
        string sibling = Path.Combine(_base, "data-other");

        PathConfinement.IsWithin(root, sibling).Should().BeFalse();
    }

    [Fact]
    public void IsWithin_RootItself_IsAllowed()
    {
        PathConfinement.IsWithin(_root, _root).Should().BeTrue();
    }

    [Fact]
    public void ResolveWithin_LinkInsideRootPointingOut_IsRejected()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside))
            return; // platform will not create links unprivileged; nothing to assert

        // The reported bug: Path.GetFullPath leaves this lexically under the root, so the old
        // StartsWith check returned true and the walk read the target's contents.
        PathConfinement.ResolveWithin(_root, link).Should().BeNull();
        PathConfinement.ResolveWithin(_root, Path.Combine(link, "keyring.txt")).Should().BeNull();
    }

    [Fact]
    public void CombineWithin_SubPathThroughALink_IsRejected()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside))
            return;

        PathConfinement.CombineWithin(_root, "escape/keyring.txt").Should().BeNull();
    }

    [Fact]
    public void IsLink_DetectsADirectoryLink()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside))
            return;

        PathConfinement.IsLink(link).Should().BeTrue();
        PathConfinement.IsLink(_root).Should().BeFalse();
    }
}
