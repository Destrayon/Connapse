using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="McpTools.UploadFile"/> and
/// <see cref="McpTools.EnsureIntermediateFoldersAsync"/>.
/// Verifies that intermediate folder entries are created during upload.
/// </summary>
[Trait("Category", "Unit")]
public class McpToolsUploadFileTests
{
    private readonly IFolderStore _folderStore;

    public McpToolsUploadFileTests()
    {
        _folderStore = Substitute.For<IFolderStore>();

        // By default, folders don't exist yet
        _folderStore
            .ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _folderStore
            .CreateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Folder(
                Guid.NewGuid().ToString(),
                ci.ArgAt<Guid>(0).ToString(),
                ci.ArgAt<string>(1),
                DateTime.UtcNow));
    }

    [Fact]
    public async Task EnsureIntermediateFolders_CreatesAllSegments()
    {
        var containerId = Guid.NewGuid();

        await McpTools.EnsureIntermediateFoldersAsync(
            _folderStore, containerId, "/a/b/c/", CancellationToken.None);

        await _folderStore.Received(1).CreateAsync(containerId, "/a/", Arg.Any<CancellationToken>());
        await _folderStore.Received(1).CreateAsync(containerId, "/a/b/", Arg.Any<CancellationToken>());
        await _folderStore.Received(1).CreateAsync(containerId, "/a/b/c/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureIntermediateFolders_SkipsExistingFolders()
    {
        var containerId = Guid.NewGuid();

        // /a/ already exists
        _folderStore
            .ExistsAsync(containerId, "/a/", Arg.Any<CancellationToken>())
            .Returns(true);

        await McpTools.EnsureIntermediateFoldersAsync(
            _folderStore, containerId, "/a/b/", CancellationToken.None);

        await _folderStore.DidNotReceive().CreateAsync(containerId, "/a/", Arg.Any<CancellationToken>());
        await _folderStore.Received(1).CreateAsync(containerId, "/a/b/", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureIntermediateFolders_RootPath_DoesNothing()
    {
        var containerId = Guid.NewGuid();

        await McpTools.EnsureIntermediateFoldersAsync(
            _folderStore, containerId, "/", CancellationToken.None);

        await _folderStore.DidNotReceive().CreateAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureIntermediateFolders_SingleLevel_CreatesOneFolder()
    {
        var containerId = Guid.NewGuid();

        await McpTools.EnsureIntermediateFoldersAsync(
            _folderStore, containerId, "/docs/", CancellationToken.None);

        await _folderStore.Received(1).CreateAsync(containerId, "/docs/", Arg.Any<CancellationToken>());
    }
}
