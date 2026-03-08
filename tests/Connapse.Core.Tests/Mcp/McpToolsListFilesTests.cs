using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

/// <summary>
/// Unit tests for <see cref="McpTools.ListFiles"/>.
/// Verifies that folder structure is derived from document paths
/// even when explicit folder entries are missing.
/// </summary>
[Trait("Category", "Unit")]
public class McpToolsListFilesTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IFolderStore _folderStore;
    private readonly IServiceProvider _services;

    public McpToolsListFilesTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _folderStore = Substitute.For<IFolderStore>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        // Default: no explicit folders, no documents
        _folderStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Folder>());

        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IFolderStore)).Returns(_folderStore);
        _services = services;
    }

    [Fact]
    public async Task ListFiles_DerivesVirtualFoldersFromDocumentPaths()
    {
        // Documents exist at /research/competitive-landscape/report.pdf
        // but no folder entries exist
        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>
            {
                MakeDocument("/research/competitive-landscape/report.pdf", "report.pdf")
            });

        var result = await McpTools.ListFiles(_services, ContainerId.ToString(), "/");

        result.Should().Contain("[DIR]  research/");
        result.Should().NotContain("report.pdf"); // Not a direct child of /
    }

    [Fact]
    public async Task ListFiles_ShowsFilesAtCorrectLevel()
    {
        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>
            {
                MakeDocument("/docs/readme.txt", "readme.txt")
            });

        var result = await McpTools.ListFiles(_services, ContainerId.ToString(), "/docs/");

        result.Should().Contain("[FILE] readme.txt");
    }

    [Fact]
    public async Task ListFiles_MergesExplicitAndDerivedFolders()
    {
        // Explicit folder /alpha/ exists
        _folderStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Folder>
            {
                new(Guid.NewGuid().ToString(), ContainerId.ToString(), "/alpha/", DateTime.UtcNow)
            });

        // Document exists under /beta/ (no explicit folder entry)
        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>
            {
                MakeDocument("/beta/file.txt", "file.txt")
            });

        var result = await McpTools.ListFiles(_services, ContainerId.ToString(), "/");

        result.Should().Contain("[DIR]  alpha/");
        result.Should().Contain("[DIR]  beta/");
    }

    [Fact]
    public async Task ListFiles_NoDuplicateFolderEntries()
    {
        // Explicit folder /research/ exists AND documents under /research/ exist
        _folderStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Folder>
            {
                new(Guid.NewGuid().ToString(), ContainerId.ToString(), "/research/", DateTime.UtcNow)
            });

        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>
            {
                MakeDocument("/research/doc.pdf", "doc.pdf")
            });

        var result = await McpTools.ListFiles(_services, ContainerId.ToString(), "/");

        // Should only appear once (HashSet deduplication)
        var count = result.Split("[DIR]  research/").Length - 1;
        count.Should().Be(1);
    }

    [Fact]
    public async Task ListFiles_IncludesDocumentIdInFileEntries()
    {
        var docId = Guid.NewGuid();
        _documentStore
            .ListAsync(ContainerId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document>
            {
                MakeDocument("/notes.md", "notes.md", docId)
            });

        var result = await McpTools.ListFiles(_services, ContainerId.ToString(), "/");

        result.Should().Contain($"ID: {docId}");
        result.Should().Contain($"[FILE] notes.md (1,024 bytes) ID: {docId}");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Document MakeDocument(string path, string fileName, Guid? id = null) => new(
        Id: (id ?? Guid.NewGuid()).ToString(),
        ContainerId: ContainerId.ToString(),
        FileName: fileName,
        ContentType: "application/octet-stream",
        Path: path,
        SizeBytes: 1024,
        CreatedAt: DateTime.UtcNow,
        Metadata: new());
}
