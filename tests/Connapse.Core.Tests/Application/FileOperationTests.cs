using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The file operations' fixed cases: the text each failure gives (REST's problem detail, and what MCP shows), and the
/// edges random inputs reach rarely: the root folder, a folder a path implies, a stream that can't seek, a connector to
/// dispose, and the bulk limit's boundary.
/// </summary>
[Trait("Category", "Unit")]
public class FileOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Here = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");
    private const string Id = "7b1c9e2a-0000-4000-8000-0000000000d1";

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IFolderStore _folders = Substitute.For<IFolderStore>();
    private readonly IDocumentAccess _access = Substitute.For<IDocumentAccess>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();

    public FileOperationTests()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _containers.GetAsync(Here, Arg.Any<CancellationToken>()).Returns(new Container(Here.ToString(), "here", null, Epoch, Epoch));
        _access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(true);
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlySet<string>)c.Arg<IReadOnlyList<Document>>().Select(d => d.Id).ToHashSet());
    }

    private static Document Doc(string path, string status = "Ready", Dictionary<string, string>? metadata = null) =>
        new(Id, Here.ToString(), Path.GetFileName(path), "text/plain", path, 4, Epoch, metadata ?? new() { ["Status"] = status });

    private void Deny() => _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);

    private DeleteFile Delete() => new(_containers, _documents, _folders, Substitute.For<IManagedStorageProvider>(),
        Substitute.For<IIngestionQueue>(), Substitute.For<IAuditLogger>(), _authorizer, NullLogger<DeleteFile>.Instance);

    private GetFileContent Content(IManagedStorageProvider storage, params IDocumentParser[] parsers) =>
        new(_containers, _documents, _access, storage, parsers, _authorizer);

    private static IManagedStorageProvider Storage(IConnector connector)
    {
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Here.ToString()).Returns(connector);
        return storage;
    }

    // ── what each refusal says ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refusals_SayWhatWasNotAllowed()
    {
        Deny();
        var storage = Substitute.For<IManagedStorageProvider>();

        (await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new())).Detail
            .Should().Be("You don't have permission to view files.");
        (await new GetFile(_containers, _documents, _access, _authorizer).RunAsync(Caller, Here, Id)).Detail
            .Should().Be("You don't have permission to view files.");
        (await Content(storage).RunAsync(Caller, Here, Id)).Detail.Should().Be("You don't have permission to view files.");
        (await Delete().RunAsync(Caller, Here, Id)).Detail.Should().Be("You don't have permission to delete files.");
        (await new BulkDeleteFiles(_containers, Delete(), _authorizer).RunAsync(Caller, Here, [Id])).Detail
            .Should().Be("You don't have permission to delete files.");
        (await new CheckFileReindex(_containers, _documents, _access, Substitute.For<IReindexService>(), _authorizer).RunAsync(Caller, Here, Id)).Detail
            .Should().Be("You don't have permission to view files.");
        (await new RetryFile(_containers, _documents, Substitute.For<IIngestionQueue>(), Substitute.For<IAuditLogger>(), _authorizer).RunAsync(Caller, Here, Id)).Detail
            .Should().Be("You don't have permission to retry files.");
    }

    [Theory]
    [InlineData(-1, 10, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_SaysWhichBound(int skip, int take, string detail)
    {
        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(null, skip, take));

        listed.Status.Should().Be(FileStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task MissingContainerAndFile_NameWhatIsMissing()
    {
        var missing = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000cc");

        (await new GetFile(_containers, _documents, _access, _authorizer).RunAsync(Caller, missing, Id)).Detail
            .Should().Be($"Container {missing} not found");
        (await new GetFile(_containers, _documents, _access, _authorizer).RunAsync(Caller, Here, "d9")).Detail
            .Should().Be($"File d9 not found in container {Here}");
    }

    [Theory]
    [InlineData("Pending", FileStatus.NotReady, "Document is still being ingested (status: Pending)")]
    [InlineData("Queued", FileStatus.NotReady, "Document is still being ingested (status: Queued)")]
    [InlineData("Failed", FileStatus.IngestionFailed, "Document failed ingestion: unknown error")]
    public async Task Content_NotYetReadable_SaysWhy(string status, FileStatus expected, string detail)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.md", status));

        var read = await Content(Substitute.For<IManagedStorageProvider>()).RunAsync(Caller, Here, Id);

        read.Status.Should().Be(expected);
        read.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task Content_NoParserOrUnreadable_SaysWhy()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.xyz"));
        (await Content(Substitute.For<IManagedStorageProvider>()).RunAsync(Caller, Here, Id)).Detail
            .Should().Be("No parser available for '.xyz' files");

        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.md"));
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync("/a.md", Arg.Any<CancellationToken>()).Returns<Stream>(_ => throw new FileNotFoundException());
        (await Content(Storage(connector)).RunAsync(Caller, Here, Id)).Detail
            .Should().Be("The backing file could not be read from storage");
    }

    // ── edges ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_NoPath_ListsTheRootAndNamesAnImpliedFoldersPath()
    {
        _folders.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Folder>());
        _documents.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document> { Doc("/reports/2026/q1.md"), Doc("/top.md") });

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery());

        listed.Result!.Entries.Should().BeEquivalentTo(
            [new FileEntry("reports", "/reports/", true, null, null, null, null), new FileEntry("top.md", "/top.md", false, 4, Epoch, Id, Doc("/top.md"))],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task List_ADocumentDirectlyHere_IsAFileNotAFolder()
    {
        _folders.ListAsync(Here, "/docs/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Folder>());
        _documents.ListAsync(Here, "/docs/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Document> { Doc("/docs/a.md") });

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery("/docs"));

        listed.Result!.Entries.Should().ContainSingle().Which.IsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task List_ADocumentTheCallerMayNotRead_IsNeitherListedNorImpliesItsFolder()
    {
        var secret = Doc("/secret/plan.md") with { Id = "7b1c9e2a-0000-4000-8000-0000000000e1" };
        _folders.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Folder>());
        _documents.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Document> { secret, Doc("/top.md") });
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { Id });

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery());

        listed.Result!.Entries.Should().ContainSingle().Which.Name.Should().Be("top.md");
        listed.Result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task ReindexCheck_ADocumentTheCallerMayNotRead_IsNotFound()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.md"));
        _access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(false);
        var reindex = Substitute.For<IReindexService>();

        var checkedFile = await new CheckFileReindex(_containers, _documents, _access, reindex, _authorizer).RunAsync(Caller, Here, Id);

        checkedFile.Status.Should().Be(FileStatus.NotFound);
        reindex.ReceivedCalls().Should().BeEmpty();
    }

    /// <summary>A storage stream that can't seek, as MinIO's and S3's can't.</summary>
    private sealed class ForwardOnly(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Content_AStreamThatCantSeek_IsBufferedForTheParserAndTheConnectorDisposed()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.pdf"));
        var connector = Substitute.For<IConnector, IDisposable>();
        connector.ReadFileAsync("/a.pdf", Arg.Any<CancellationToken>()).Returns(new ForwardOnly(Encoding.UTF8.GetBytes("%PDF")));
        var parser = Substitute.For<IDocumentParser>();
        parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf" });
        bool? seekable = null;
        parser.ParseAsync(Arg.Do<Stream>(s => seekable = s.CanSeek), "a.pdf", Arg.Any<CancellationToken>())
            .Returns(c => new ParsedDocument(new StreamReader(c.Arg<Stream>()).ReadToEnd(), [], []));

        var read = await Content(Storage(connector), parser).RunAsync(Caller, Here, Id);

        read.Result!.Content.Should().Be("%PDF");
        seekable.Should().BeTrue("a parser such as PdfPig needs to seek");
        ((IDisposable)connector).Received(1).Dispose();
    }

    [Fact]
    public async Task Content_TextFile_IsReadAsUtf8WithoutAParser()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.md"));
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync("/a.md", Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("héllo")));

        (await Content(Storage(connector)).RunAsync(Caller, Here, Id)).Result!.Content.Should().Be("héllo");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task BulkDelete_AtMost100Files(int count, bool allowed)
    {
        var deleted = await new BulkDeleteFiles(_containers, Delete(), _authorizer)
            .RunAsync(Caller, Here, [.. Enumerable.Range(0, count).Select(i => $"id-{i}")]);

        deleted.Succeeded.Should().Be(allowed);
        if (!allowed)
            deleted.Detail.Should().Be("A bulk delete must name at most 100 files");
    }

    [Fact]
    public async Task BulkDelete_NoFiles_SaysOneIsRequired()
    {
        var deleted = await new BulkDeleteFiles(_containers, Delete(), _authorizer).RunAsync(Caller, Here, []);

        deleted.Detail.Should().Be("At least one file id is required");
    }
}
