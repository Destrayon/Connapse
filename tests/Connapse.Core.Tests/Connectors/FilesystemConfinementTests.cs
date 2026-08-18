using System.Runtime.InteropServices;
using Connapse.Core;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// End-to-end confinement of <see cref="FilesystemConnector"/> (#365).
/// <para>
/// PathConfinementTests cover the helper in isolation; these pin the connector itself, which
/// is where the leak actually happened — the guard admitted a junction and
/// <c>Directory.EnumerateFiles(.., SearchOption.AllDirectories)</c> then read through it.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FilesystemConfinementTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), $"connapse-fsguard-{Guid.NewGuid():N}");

    private readonly string _root;
    private readonly string _outside;

    public FilesystemConfinementTests()
    {
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);

        File.WriteAllText(Path.Combine(_root, "legitimate.md"), "indexable");
        File.WriteAllText(Path.Combine(_outside, "keyring.txt"), "SECRET-KEYRING-CONTENT");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.GetDirectories(_root))
            {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(dir);
            }
        }

        try { Directory.Delete(_base, recursive: true); } catch (IOException) { }
    }

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

    private FilesystemConnector Connector() =>
        new(new FilesystemConnectorConfig { RootPath = _root });

    [Fact]
    public async Task ListFilesAsync_DoesNotDescendThroughALink()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside))
            return; // platform will not create links unprivileged

        var files = await Connector().ListFilesAsync();

        files.Select(f => Path.GetFileName(f.Path))
            .Should().NotContain("keyring.txt", "a link out of the root must not be indexed");
        files.Should().ContainSingle(f => Path.GetFileName(f.Path) == "legitimate.md",
            "ordinary files inside the root must still be listed");
    }

    [Fact]
    public async Task ReadFileAsync_ThroughALink_IsRefused()
    {
        string link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside))
            return;

        var act = async () => await Connector().ReadFileAsync("escape/keyring.txt");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ReadFileAsync_AbsolutePathOutsideRoot_IsRefused()
    {
        // This used to return early for any rooted path, "for watcher events", handing back
        // whatever was asked for with no containment check at all.
        string outsideFile = Path.Combine(_outside, "keyring.txt");

        var act = async () => await Connector().ReadFileAsync(outsideFile);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ReadFileAsync_OrdinaryFileInsideRoot_StillWorks()
    {
        // The guard must not be so strict it breaks the normal path.
        using var stream = await Connector().ReadFileAsync("legitimate.md");
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("indexable");
    }

    [Fact]
    public async Task ReadFileAsync_DotDotTraversal_IsRefused()
    {
        var act = async () => await Connector().ReadFileAsync("../outside/keyring.txt");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }
}
