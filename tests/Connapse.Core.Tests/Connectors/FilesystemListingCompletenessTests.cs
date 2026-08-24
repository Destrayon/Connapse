using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

/// <summary>
/// The listing must never be silently short (#390).
/// <para>
/// A reconcile treats "indexed but absent from the listing" as deleted, so a connector that
/// returns fewer files than exist is handing the sync engine a deletion instruction it never
/// meant to give. The deletion guard bounds the damage, but it exists for the unforeseeable
/// remote failure — not to compensate for our own connector returning a knowingly wrong
/// answer, and a source below the guard's floor is not protected at all.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class FilesystemListingCompletenessTests : IDisposable
{
    private readonly string _base = Path.Combine(
        Path.GetTempPath(), $"connapse-fslisting-{Guid.NewGuid():N}");

    public FilesystemListingCompletenessTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Test cleanup only; a locked file here must not fail the run.
        }
    }

    private static FilesystemConnector Connector(string root) =>
        new(new FilesystemConnectorConfig { RootPath = root });

    /// <summary>
    /// The Docker case, and the reason "the filesystem connector does not work" had no error
    /// attached to it: the container cannot see the host's disk, so the root is simply absent.
    /// </summary>
    [Fact]
    public async Task ListFiles_MissingRoot_ThrowsRatherThanReturningEmpty()
    {
        string missing = Path.Combine(_base, "not-created");

        Func<Task> act = () => Connector(missing).ListFilesAsync();

        (await act.Should().ThrowAsync<DirectoryNotFoundException>())
            .WithMessage("*does not exist*")
            .And.Message.Should().Contain("container",
                "this is nearly always hit in Docker, and the message is the only thing an "
                + "operator gets");
    }

    /// <summary>
    /// The distinction that has to survive the fix. A genuinely empty directory is not an
    /// error — it means the source has no files, which is a real and correct answer. Only a
    /// root that is *absent* is unanswerable.
    /// </summary>
    [Fact]
    public async Task ListFiles_EmptyButExistingRoot_StillReturnsEmptySuccessfully()
    {
        string empty = Path.Combine(_base, "empty");
        Directory.CreateDirectory(empty);

        (await Connector(empty).ListFilesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ListFiles_OrdinaryTree_IsUnaffected()
    {
        string root = Path.Combine(_base, "ordinary");
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "a.md"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "b.md"), "bravo");

        var files = await Connector(root).ListFilesAsync();

        files.Select(f => Path.GetFileName(f.Path)).Should().BeEquivalentTo("a.md", "b.md");
    }

    /// <summary>
    /// A prefix naming a directory that is not there is the same unanswerable question as a
    /// missing root, and must fail the same way rather than reporting an empty subtree.
    /// </summary>
    [Fact]
    public async Task ListFiles_MissingPrefix_AlsoThrows()
    {
        string root = Path.Combine(_base, "with-prefix");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a.md"), "alpha");

        Func<Task> act = () => Connector(root).ListFilesAsync("no-such-subdir");

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    /// <summary>
    /// Unix only, and only when the test process is not root.
    /// </summary>
    /// <remarks>
    /// The precondition is checked rather than assumed. An earlier version of this test only
    /// skipped Windows, so in a container — where tests run as root and permissions do not
    /// apply — it made a directory "unreadable", read it anyway, and reported a pass. A test
    /// that cannot establish the condition it is testing has to say so by not running, never
    /// by passing.
    /// <para>
    /// The behaviour it covers is verified independently: with
    /// <c>IgnoreInaccessible = false</c>, <c>Directory.EnumerateFiles</c> throws
    /// <see cref="UnauthorizedAccessException"/> on a subdirectory it cannot open, which the
    /// connector turns into the partial-listing refusal asserted below.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ListFiles_UnreadableSubdirectory_FailsRatherThanReturningFewerFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(_base, "partly-unreadable");
        string locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);

        await File.WriteAllTextAsync(Path.Combine(root, "visible.md"), "visible");
        await File.WriteAllTextAsync(Path.Combine(locked, "hidden.md"), "hidden");

        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            if (CanStillRead(locked))
                return;

            Func<Task> act = () => Connector(root).ListFilesAsync();

            (await act.Should().ThrowAsync<IOException>())
                .WithMessage("*partial listing*");
        }
        finally
        {
            // Restored so Dispose can delete it.
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// True when the process can read <paramref name="path"/> despite it being chmod 000 —
    /// which means the process is root and this environment cannot express the condition.
    /// </summary>
    private static bool CanStillRead(string path)
    {
        try
        {
            Directory.GetFiles(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
