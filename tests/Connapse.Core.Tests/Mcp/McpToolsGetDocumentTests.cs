using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Web.Mcp;
using Connapse.Web.Services;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsGetDocumentTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private const string DocId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string FileName = "readme.md";
    private const string FilePath = "/docs/readme.md";
    private const string FileContent = "# Hello World\n\nThis is a test document.";

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IManagedStorageProvider _managedStorage;
    private readonly IConnector _connector;
    private readonly IServiceProvider _services;

    public McpToolsGetDocumentTests()
    {
        _containerStore = ContainerStoreFakes.Create();
        _documentStore = Substitute.For<IDocumentStore>();
        _documentStore.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, string?>)_uris);
        _documentStore.GetUnaddressedAdmittedAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.Arg<IReadOnlyCollection<string>>().ToHashSet());
        _managedStorage = Substitute.For<IManagedStorageProvider>();
        _connector = Substitute.For<IConnector>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        _managedStorage
            .CreateConnector(Arg.Any<string>())
            .Returns(_connector);

        _connector
            .ReadFileAsync(FilePath, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(Encoding.UTF8.GetBytes(FileContent)));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IManagedStorageProvider)).Returns(_managedStorage);
        services.GetService(typeof(DocumentReadGuard)).Returns(NewGuard(new NoOpSearchResultVerifier()));
        _services = services;
    }

    [Fact]
    public async Task GetDocument_ById_ReturnsContentWithHeader()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument());

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().Contain($"Document: {FileName}");
        result.Should().Contain($"Path: {FilePath}");
        result.Should().Contain($"ID: {DocId}");
        result.Should().Contain("---");
        result.Should().Contain(FileContent);
    }

    [Fact]
    public async Task GetDocument_ByPath_ReturnsContent()
    {
        _documentStore.GetByPathAsync(ContainerId, FilePath, Arg.Any<CancellationToken>())
            .Returns(MakeDocument());

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), FilePath);

        result.Should().Contain(FileContent);
        result.Should().Contain($"Document: {FileName}");
    }

    [Fact]
    public async Task GetDocument_GuardDenies_ReturnsSameErrorAsMissingDocument()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>()).Returns(MakeDocument());
        _uris[DocId] = "atlassian://00000000-0000-0000-0000-000000000001/confluence/page/1";
        var denying = Substitute.For<ISearchResultVerifier>();
        denying.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SearchHit>());
        _services.GetService(typeof(DocumentReadGuard)).Returns(NewGuard(denying));

        var denied = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>()).Returns((Document?)null);
        var missing = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        denied.Should().Be(missing);
        await _connector.DidNotReceive().ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDocument_VerifierThrows_ReturnsSameErrorAsMissingDocument()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>()).Returns(MakeDocument());
        _uris[DocId] = "atlassian://00000000-0000-0000-0000-000000000001/confluence/page/1";
        var throwing = Substitute.For<ISearchResultVerifier>();
        throwing.VerifyAsync(Arg.Any<IReadOnlyList<SearchHit>>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("site down"));
        _services.GetService(typeof(DocumentReadGuard)).Returns(NewGuard(throwing));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:").And.Contain("not found");
    }

    [Fact]
    public async Task GetDocument_NoGuardRegistered_RefusesTheRead()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>()).Returns(MakeDocument());
        _services.GetService(typeof(DocumentReadGuard)).Returns(null);

        (await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId)).Should().StartWith("Error:").And.Contain("not found");
    }

    [Fact]
    public async Task GetDocument_ContainerNotFound_ReturnsError()
    {
        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, Guid.NewGuid().ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task GetDocument_DocumentNotFound_ReturnsError()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task GetDocument_WrongContainer_ReturnsError()
    {
        var otherContainerId = Guid.NewGuid();
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(containerId: otherContainerId));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found in this container");
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Queued", true)]
    [InlineData("Ready", false)]
    public async Task GetDocument_TextFileWaitingToBeIndexed_ReturnsItsTextAndSaysSearchCantFindItYet(string status, bool waiting)
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(status: status));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().EndWith(FileContent);
        if (waiting)
            result.Should().Contain("Indexing: still waiting, so search_knowledge can't find this document yet.\n---\n");
        else
            result.Should().NotContain("Indexing:");
    }

    [Fact]
    public async Task GetDocument_ParsedTypeStillIngesting_ReturnsError()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(status: "Pending", fileName: "report.pdf", path: "/docs/report.pdf"));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("still being ingested");
    }

    [Fact]
    public async Task GetDocument_FailedStatus_ReturnsError()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(status: "Failed", errorMessage: "parse error"));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("parse error");
    }

    [Fact]
    public async Task GetDocument_FileNotInStorage_ReturnsError()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument());

        _connector
            .ReadFileAsync(FilePath, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("gone"));

        var result = await McpTools.GetDocument(McpCaller.Files(_services).Get, McpCaller.Files(_services).Content, McpCaller.Files(_services).Tables, McpCaller.Files(_services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("could not be read from storage");
    }

    [Fact]
    public async Task GetDocument_BinaryFile_UsesParsers()
    {
        const string pdfPath = "/docs/report.pdf";
        const string parsedText = "Extracted PDF text content.";

        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(fileName: "report.pdf", path: pdfPath));

        _connector
            .ReadFileAsync(pdfPath, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 })); // %PDF header

        var parser = Substitute.For<IDocumentParser>();
        parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf" });
        parser.ParseAsync(Arg.Any<Stream>(), "report.pdf", Arg.Any<CancellationToken>())
            .Returns(new ParsedDocument(parsedText, new(), new()));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IManagedStorageProvider)).Returns(_managedStorage);
        services.GetService(typeof(DocumentReadGuard)).Returns(NewGuard(new NoOpSearchResultVerifier()));
        services.GetService(typeof(IEnumerable<IDocumentParser>)).Returns(new[] { parser });

        var result = await McpTools.GetDocument(McpCaller.Files(services).Get, McpCaller.Files(services).Content, McpCaller.Files(services).Tables, McpCaller.Files(services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().Contain(parsedText);
        result.Should().Contain("Document: report.pdf");
    }

    [Fact]
    public async Task GetDocument_NoParserForExtension_ReturnsError()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>())
            .Returns(MakeDocument(fileName: "data.xyz", path: "/data.xyz"));

        _connector
            .ReadFileAsync("/data.xyz", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 0x00 }));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IManagedStorageProvider)).Returns(_managedStorage);
        services.GetService(typeof(DocumentReadGuard)).Returns(NewGuard(new NoOpSearchResultVerifier()));
        services.GetService(typeof(IEnumerable<IDocumentParser>)).Returns(Array.Empty<IDocumentParser>());

        var result = await McpTools.GetDocument(McpCaller.Files(services).Get, McpCaller.Files(services).Content, McpCaller.Files(services).Tables, McpCaller.Files(services).Container, McpCaller.Http, ContainerId.ToString(), DocId);

        result.Should().StartWith("Error:");
        result.Should().Contain("No parser available");
    }

    // ── get_documents ─────────────────────────────────────────────────────────────────────────────

    private Task<string> GetDocuments(string files, string? containerId = null) =>
        McpTools.GetDocuments(McpCaller.Files(_services).Contents, McpCaller.Files(_services).Container, McpCaller.Http,
            containerId ?? ContainerId.ToString(), files);

    [Fact]
    public async Task GetDocuments_ReadsEachFileInOrderAndAnswersAMissingOneInPlace()
    {
        _documentStore.GetByPathAsync(ContainerId, FilePath, Arg.Any<CancellationToken>()).Returns(MakeDocument());

        var result = await GetDocuments($"[\"{FilePath}\", \"/docs/missing.md\"]");

        result.Should().StartWith("Read 1 of 2 document(s).");
        result.Should().Contain($"=== {FileName} ===\nPath: {FilePath}\nID: {DocId}\nDates: added ");
        result.Should().Contain(FileContent);
        result.Should().EndWith($"=== /docs/missing.md ===\nError: File /docs/missing.md not found in container {ContainerId}");
        result.IndexOf(FileContent, StringComparison.Ordinal).Should().BeLessThan(result.IndexOf("missing.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDocuments_EmptyFile_SaysSoInsteadOfAnEmptyBody()
    {
        _documentStore.GetAsync(DocId, Arg.Any<CancellationToken>()).Returns(MakeDocument());
        _connector.ReadFileAsync(FilePath, Arg.Any<CancellationToken>()).Returns(new MemoryStream());

        (await GetDocuments($"[\"{DocId}\"]")).Should().EndWith("---\n(no readable text content)");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"a\":1}")]
    public async Task GetDocuments_NotAJsonArray_ReturnsError(string files) =>
        (await GetDocuments(files)).Should().Be("Error: 'files' must be a valid JSON array of strings.");

    [Fact]
    public async Task GetDocuments_EmptyOrTooMany_ReturnsTheOperationsError()
    {
        (await GetDocuments("[]")).Should().Be("Error: At least one file is required");
        string many = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 51).Select(i => $"/f{i}.md"));
        (await GetDocuments(many)).Should().Be("Error: A maximum of 50 files can be read in one call");
    }

    [Fact]
    public async Task GetDocuments_ContainerNotFound_ReturnsError() =>
        (await GetDocuments($"[\"{DocId}\"]", Guid.NewGuid().ToString())).Should().StartWith("Error:").And.Contain("not found");

    private readonly Dictionary<string, string?> _uris = new() { [DocId] = null };

    private DocumentReadGuard NewGuard(ISearchResultVerifier verifier) =>
        new(verifier, _documentStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentReadGuard>.Instance);

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Document MakeDocument(
        Guid? containerId = null,
        string? status = null,
        string? errorMessage = null,
        string? fileName = null,
        string? path = null)
    {
        var meta = new Dictionary<string, string>
        {
            ["Status"] = status ?? "Ready",
            ["ContentHash"] = "abc123",
            ["ChunkCount"] = "3"
        };
        if (errorMessage is not null)
            meta["ErrorMessage"] = errorMessage;

        return new(
            Id: DocId,
            ContainerId: (containerId ?? ContainerId).ToString(),
            FileName: fileName ?? FileName,
            ContentType: "text/markdown",
            Path: path ?? FilePath,
            SizeBytes: 100,
            CreatedAt: DateTime.UtcNow,
            Metadata: meta);
    }
}
