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
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

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

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
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
        Substitute.For<IIngestionQueue>(), Substitute.For<IStoredFileRemovalStore>(), Substitute.For<IAuditLogger>(), _authorizer, NullLogger<DeleteFile>.Instance);

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
    [InlineData("/a.pdf", "Pending", FileStatus.NotReady, "Document is still being ingested (status: Pending)")]
    [InlineData("/a.pdf", "Queued", FileStatus.NotReady, "Document is still being ingested (status: Queued)")]
    [InlineData("/a.md", "Failed", FileStatus.IngestionFailed, "Document failed ingestion: unknown error")]
    public async Task Content_NotYetReadable_SaysWhy(string path, string status, FileStatus expected, string detail)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc(path, status));

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

    [Fact]
    public async Task Contents_RefusalsAndBadLists_SayWhatIsWrong()
    {
        var contents = new GetFileContents(_containers, Content(Substitute.For<IManagedStorageProvider>()), _authorizer);

        (await contents.RunAsync(Caller, Here, [])).Should().BeEquivalentTo(new { Status = FileStatus.Invalid, Detail = "At least one file is required", Field = "files" });
        (await contents.RunAsync(Caller, Here, [.. Enumerable.Repeat(Id, 51)])).Detail.Should().Be("A maximum of 50 files can be read in one call");
        (await contents.RunAsync(Caller, Here, [Id, ""])).Detail.Should().Be("Every file must be an id or a path");
        (await contents.RunAsync(Caller, Guid.NewGuid(), [Id])).Status.Should().Be(FileStatus.ContainerNotFound);
        Deny();
        (await contents.RunAsync(Caller, Here, [Id])).Detail.Should().Be("You don't have permission to view files.");
    }

    [Fact]
    public async Task Contents_ExactlyTheLimit_IsReadAndEachFailureKeepsItsStatus()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.pdf", "Pending"));
        var contents = new GetFileContents(_containers, Content(Substitute.For<IManagedStorageProvider>()), _authorizer);

        var read = await contents.RunAsync(Caller, Here, [.. Enumerable.Repeat(Id, GetFileContents.MaxFiles)]);

        read.Result.Should().HaveCount(GetFileContents.MaxFiles).And.OnlyContain(i =>
            i.Status == FileStatus.NotReady && i.Content == null && i.Error == "Document is still being ingested (status: Pending)");
    }

    [Fact]
    public async Task Contents_PastTheTotalLimit_AFileGetsAnErrorAndSmallerOnesStillFit()
    {
        _documents.GetByPathAsync(Here, "/big.md", Arg.Any<CancellationToken>()).Returns(Doc("/big.md"));
        _documents.GetByPathAsync(Here, "/small.md", Arg.Any<CancellationToken>()).Returns(Doc("/small.md"));
        string big = new('x', GetFileContents.MaxTotalChars - 5);
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync("/big.md", Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(big)));
        connector.ReadFileAsync("/small.md", Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("small")));
        var contents = new GetFileContents(_containers, Content(Storage(connector)), _authorizer);

        var read = await contents.RunAsync(Caller, Here, ["big.md", "big.md", "small.md", "small.md"]);

        read.Result!.Select(i => (i.Status, i.Content?.Content.Length, i.Error)).Should().Equal(
            (FileStatus.Success, big.Length, null),
            (FileStatus.TooLarge, null, "Skipped: this file's text would take the call past the maximum of 2000000 characters; read it in a call of its own"),
            (FileStatus.Success, 5, null),
            (FileStatus.TooLarge, null, "Skipped: this file's text would take the call past the maximum of 2000000 characters; read it in a call of its own"));
    }

    [Fact]
    public async Task Contents_AParserThatThrows_FailsOnlyThatFile()
    {
        _documents.GetByPathAsync(Here, "/a.pdf", Arg.Any<CancellationToken>()).Returns(Doc("/a.pdf"));
        _documents.GetByPathAsync(Here, "/b.md", Arg.Any<CancellationToken>()).Returns(Doc("/b.md"));
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("text")));
        var parser = Substitute.For<IDocumentParser>();
        parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf" });
        parser.ParseAsync(Arg.Any<Stream>(), "a.pdf", Arg.Any<CancellationToken>()).Returns<ParsedDocument>(_ => throw new InvalidDataException("corrupt"));
        var contents = new GetFileContents(_containers, Content(Storage(connector), parser), _authorizer);

        var read = await contents.RunAsync(Caller, Here, ["a.pdf", "b.md"]);

        read.Result!.Select(i => (i.Status, i.Content?.Content, i.Error)).Should().Equal(
            (FileStatus.NotReadable, null, "The file's text could not be read"),
            (FileStatus.Success, "text", null));
    }

    [Fact]
    public async Task Contents_Cancelled_StopsTheCall()
    {
        _documents.GetByPathAsync(Here, "/a.pdf", Arg.Any<CancellationToken>()).Returns(Doc("/a.pdf"));
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("text")));
        var parser = Substitute.For<IDocumentParser>();
        parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf" });
        parser.ParseAsync(Arg.Any<Stream>(), "a.pdf", Arg.Any<CancellationToken>()).Returns<ParsedDocument>(_ => throw new OperationCanceledException());
        var contents = new GetFileContents(_containers, Content(Storage(connector), parser), _authorizer);

        var read = () => contents.RunAsync(Caller, Here, ["a.pdf", "a.pdf"]);

        await read.Should().ThrowAsync<OperationCanceledException>();
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

    [Theory]
    [InlineData("oldest")]
    [InlineData("")]
    public async Task List_UnknownSort_IsInvalidAndNamesTheField(string sort)
    {
        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(Sort: sort));

        listed.Status.Should().Be(FileStatus.Invalid);
        listed.Detail.Should().Be("sort must be \"name\" or \"newest\"");
        listed.Field.Should().Be("sort");
    }

    private void SeedForSorting()
    {
        _folders.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Folder> { new("f1", Here.ToString(), "/kept/", Epoch.AddDays(2)) });
        _documents.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Document>
        {
            Doc("/a.md") with { Id = "a", CreatedAt = Epoch.AddDays(1) },
            Doc("/b.md") with { Id = "b", CreatedAt = Epoch.AddDays(3) },
            Doc("/implied/c.md") with { Id = "c", CreatedAt = Epoch.AddDays(9) },
        });
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "a", "b", "c" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("name")]
    [InlineData("NAME")]
    public async Task List_SortByName_FoldersFirstThenByName(string? sort)
    {
        SeedForSorting();

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(Sort: sort));

        listed.Result!.Entries.Select(e => e.Name).Should().Equal("implied", "kept", "a.md", "b.md");
    }

    [Theory]
    [InlineData("newest")]
    [InlineData("Newest")]
    public async Task List_SortNewest_NewestFirstWithUndatedImpliedFoldersLast(string sort)
    {
        SeedForSorting();

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(Sort: sort));

        listed.Result!.Entries.Select(e => e.Name).Should().Equal("b.md", "kept", "a.md", "implied");
    }

    [Fact]
    public async Task List_SortNewestWithTake_KeepsOnlyTheNewest()
    {
        SeedForSorting();

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(Take: 2, Sort: "newest"));

        listed.Result!.Entries.Select(e => e.Name).Should().Equal("b.md", "kept");
        listed.Result.TotalCount.Should().Be(4);
        listed.Result.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task List_SameDate_TiesBreakByName()
    {
        _folders.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Folder>());
        _documents.ListAsync(Here, "/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Document>
        {
            Doc("/z.md") with { Id = "z" },
            Doc("/m.md") with { Id = "m" },
        });
        _access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "z", "m" });

        var listed = await new ListFiles(_containers, _documents, _folders, _access, _authorizer).RunAsync(Caller, Here, new ListFilesQuery(Sort: "newest"));

        listed.Result!.Entries.Select(e => e.Name).Should().Equal("m.md", "z.md");
    }

    [Fact]
    public void UpdatedAt_UsesTheUploadStampElseTheAddedDate()
    {
        DateTime appended = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        FileDates.UpdatedAt(Doc("/a.md")).Should().Be(Epoch);
        FileDates.UpdatedAt(Doc("/a.md", metadata: new() { ["UploadedAt"] = appended.ToString("O") })).Should().Be(appended);
        FileDates.UpdatedAt(Doc("/a.md", metadata: new() { ["UploadedAt"] = "2026-03-04T07:06:07+02:00" })).Should().Be(appended);
        FileDates.UpdatedAt(Doc("/a.md", metadata: new() { ["UploadedAt"] = "not a date" })).Should().Be(Epoch);
    }

    [Fact]
    public void Entry_UpdatedAt_IsTheDocumentsElseTheFoldersDate()
    {
        var stamped = Doc("/a.md", metadata: new() { ["UploadedAt"] = Epoch.AddDays(5).ToString("O") });

        new FileEntry("a.md", "/a.md", false, 4, Epoch, Id, stamped).UpdatedAt.Should().Be(Epoch.AddDays(5));
        new FileEntry("kept", "/kept/", true, null, Epoch.AddDays(2), "f1", null).UpdatedAt.Should().Be(Epoch.AddDays(2));
        new FileEntry("implied", "/implied/", true, null, null, null, null).UpdatedAt.Should().BeNull();
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
    [InlineData("Pending", true)]
    [InlineData("Queued", true)]
    [InlineData("Processing", true)]
    [InlineData("Ready", false)]
    public async Task Content_TextFileStillWaitingToBeIndexed_IsReadAndSaysItIsNotSearchableYet(string status, bool awaiting)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/journal/today.md", status));
        var connector = Substitute.For<IConnector>();
        connector.ReadFileAsync("/journal/today.md", Arg.Any<CancellationToken>()).Returns(new MemoryStream(Encoding.UTF8.GetBytes("just saved")));

        var read = await Content(Storage(connector)).RunAsync(Caller, Here, Id);

        read.Status.Should().Be(FileStatus.Success);
        read.Result!.Content.Should().Be("just saved");
        read.Result.AwaitingIndex.Should().Be(awaiting);
    }

    [Fact]
    public async Task Content_ParsedTypeStillWaitingToBeIndexed_IsNotReadFromStorage()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/a.pdf", "Processing"));
        var connector = Substitute.For<IConnector>();
        var parser = Substitute.For<IDocumentParser>();
        parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf" });

        var read = await Content(Storage(connector), parser).RunAsync(Caller, Here, Id);

        read.Status.Should().Be(FileStatus.NotReady);
        connector.ReceivedCalls().Should().BeEmpty();
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

    // ── append ────────────────────────────────────────────────────────────────────────────────────

    private readonly IUploadService _uploads = Substitute.For<IUploadService>();

    /// <summary>A writable store holding <paramref name="stored"/> at /notes/log.md, and the connector it hands out.</summary>
    private static (IManagedStorageProvider Storage, IWritableConnector Connector) Holding(string stored)
    {
        var connector = Substitute.For<IWritableConnector, IDisposable>();
        connector.ReadFileAsync("/notes/log.md", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(stored)));
        return (Storage(connector), connector);
    }

    private AppendToFile Append(IManagedStorageProvider storage, long maxFileBytes = 100)
    {
        var settings = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings { MaxFileBytes = maxFileBytes });
        return new(_containers, _documents, _access, storage, _uploads, _authorizer, settings);
    }

    [Fact]
    public async Task Append_TextFile_UploadsTheOldBytesThenTheNewToTheSamePath()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        UploadRequest? request = null;
        string? sent = null;
        // The real upload service disposes the stream it is given.
        _uploads.UploadAsync(Arg.Do<UploadRequest>(r => { request = r; sent = new StreamReader(r.Content).ReadToEnd(); r.Content.Dispose(); }), Arg.Any<CancellationToken>())
            .Returns(new UploadResult(true, Id, "job-1"));
        var (storage, connector) = Holding("first");

        var appended = await Append(storage).RunAsync(Caller, Here, Id, "\nsécond");

        appended.Result.Should().Be(new FileAppend(Id, "job-1", "/notes/log.md", Encoding.UTF8.GetByteCount("first\nsécond")));
        sent.Should().Be("first\nsécond");
        request!.Should().BeEquivalentTo(new { ContainerId = Here, FileName = "log.md", Path = "/notes/", ContentType = "text/plain", Strategy = (string?)null },
            o => o.ExcludingMissingMembers());
        ((IDisposable)connector).Received().Dispose();
    }

    [Fact]
    public async Task Append_ByPath_FindsTheFile()
    {
        _documents.GetByPathAsync(Here, "/notes/log.md", Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, Id));

        var appended = await Append(Holding("a").Storage).RunAsync(Caller, Here, "notes/log.md", "b");

        appended.Result.Should().Be(new FileAppend(Id, null, "/notes/log.md", 2));
    }

    [Theory]
    [InlineData("true", "Semantic")]
    [InlineData("false", null)]
    public async Task Append_KeepsOnlyAChunkingStrategyChosenOnPurpose(string chosen, string? kept)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md", metadata: new()
        {
            ["Status"] = "Ready",
            ["IndexedWith:ChunkingStrategy"] = "Semantic",
            ["IndexedWith:StrategyExplicit"] = chosen,
        }));
        string? strategy = "unset";
        _uploads.UploadAsync(Arg.Do<UploadRequest>(r => strategy = r.Strategy), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, Id));

        await Append(Holding("a").Storage).RunAsync(Caller, Here, Id, "b");

        strategy.Should().Be(kept);
    }

    [Fact]
    public async Task Append_Refusals_SayWhatIsWrong()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        var append = Append(Holding("a").Storage);

        (await append.RunAsync(Caller, Here, Id, "")).Should().BeEquivalentTo(new { Status = FileStatus.Invalid, Detail = "text is required", Field = "text" });
        (await append.RunAsync(Caller, Here, Id, null)).Status.Should().Be(FileStatus.Invalid);
        (await append.RunAsync(Caller, Here, " ", "b")).Should().BeEquivalentTo(new { Status = FileStatus.Invalid, Detail = "file is required", Field = "file" });
        (await append.RunAsync(Caller, Guid.NewGuid(), Id, "b")).Status.Should().Be(FileStatus.ContainerNotFound);
        (await append.RunAsync(Caller, Here, "missing.md", "b")).Status.Should().Be(FileStatus.NotFound);
        Deny();
        (await append.RunAsync(Caller, Here, Id, "b")).Should().BeEquivalentTo(new { Status = FileStatus.Forbidden, Detail = "You don't have permission to append to files." });
        await _uploads.DidNotReceiveWithAnyArgs().UploadAsync(default!, default);
    }

    [Fact]
    public async Task Append_AFileTheCallerMayNotRead_IsNotFound()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        _access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(false);

        (await Append(Holding("a").Storage).RunAsync(Caller, Here, Id, "b")).Status.Should().Be(FileStatus.NotFound);
    }

    [Fact]
    public async Task Append_MemoryOrReadOnlyContainer_IsRefused()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        var readOnly = Substitute.For<IManagedStorageProvider>();
        readOnly.CreateConnector(Here.ToString()).Returns(Substitute.For<IConnector>());

        (await Append(readOnly).RunAsync(Caller, Here, Id, "b")).Should().BeEquivalentTo(
            new { Status = FileStatus.NotWritable, Detail = "This container is not writable." });

        _containers.GetAsync(Here, Arg.Any<CancellationToken>()).Returns(new Container(Here.ToString(), "here", null, Epoch, Epoch, Kind: ContainerKind.Memory));
        (await Append(Holding("a").Storage).RunAsync(Caller, Here, Id, "b")).Status.Should().Be(FileStatus.MemoryContainer);
    }

    [Fact]
    public async Task Append_NotATextFile_NamesTheTextTypes()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/report.PDF"));

        var appended = await Append(Holding("a").Storage).RunAsync(Caller, Here, Id, "b");

        appended.Status.Should().Be(FileStatus.UnsupportedType);
        appended.Field.Should().Be("file");
        appended.Detail.Should().StartWith("Only text files can be appended to, not '.pdf' files. Text types: .css, .csv,");
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task Append_BackingFileUnreadable_IsNotReadable(Type error)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        var connector = Substitute.For<IWritableConnector>();
        connector.ReadFileAsync("/notes/log.md", Arg.Any<CancellationToken>()).Returns<Stream>(_ => throw (Exception)Activator.CreateInstance(error)!);

        var appended = await Append(Storage(connector)).RunAsync(Caller, Here, Id, "b");

        appended.Should().BeEquivalentTo(new { Status = FileStatus.NotReadable, Detail = "The backing file could not be read from storage" });
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(9, false)]
    public async Task Append_TheLimitCountsTheWholeFile(long maxFileBytes, bool allowed)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, Id));

        var appended = await Append(Holding("12345").Storage, maxFileBytes).RunAsync(Caller, Here, Id, "67890");

        appended.Succeeded.Should().Be(allowed);
        if (!allowed)
        {
            appended.Should().BeEquivalentTo(new { Status = FileStatus.TooLarge, Field = "text" }, o => o.ExcludingMissingMembers());
            appended.Detail.Should().Be(Connapse.Core.Utilities.UploadLimits.FileTooLarge(10, 9));
        }
    }

    [Fact]
    public async Task Append_AFileAlreadyAtTheLimit_IsRefusedBeforeItIsRead()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md") with { SizeBytes = 98 });
        var (storage, connector) = Holding("a");

        var appended = await Append(storage).RunAsync(Caller, Here, Id, "abc");

        appended.Should().BeEquivalentTo(new { Status = FileStatus.TooLarge, Field = "text" }, o => o.ExcludingMissingMembers());
        appended.Detail.Should().Be(Connapse.Core.Utilities.UploadLimits.FileTooLarge(101, 100));
        await connector.DidNotReceiveWithAnyArgs().ReadFileAsync(default!, default);
    }

    [Fact]
    public async Task Append_AStoredFileBiggerThanItsRecord_StopsReadingPastTheLimit()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        var (storage, _) = Holding(new string('x', 1_000_000));

        var appended = await Append(storage).RunAsync(Caller, Here, Id, "abc");

        // One 80 KiB buffer is read past the limit, and no more of the megabyte.
        appended.Status.Should().Be(FileStatus.TooLarge);
        appended.Detail.Should().Be(Connapse.Core.Utilities.UploadLimits.FileTooLarge(81920 + 3, 100));
        await _uploads.DidNotReceiveWithAnyArgs().UploadAsync(default!, default);
    }

    [Theory]
    [InlineData("utf-8 bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    [InlineData("utf-32le")]
    [InlineData("utf-32be")]
    public async Task Append_AFileWithAByteOrderMark_GetsTheTextInItsOwnEncoding(string name)
    {
        Encoding encoding = name switch
        {
            "utf-8 bom" => new UTF8Encoding(true),
            "utf-16le" => new UnicodeEncoding(false, true),
            "utf-16be" => new UnicodeEncoding(true, true),
            "utf-32le" => new UTF32Encoding(false, true),
            _ => new UTF32Encoding(true, true),
        };
        byte[] Encoded(string text) => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        var connector = Substitute.For<IWritableConnector>();
        connector.ReadFileAsync("/notes/log.md", Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoded("first")));
        byte[]? sent = null;
        _uploads.UploadAsync(Arg.Do<UploadRequest>(r => { using var copy = new MemoryStream(); r.Content.CopyTo(copy); sent = copy.ToArray(); }), Arg.Any<CancellationToken>())
            .Returns(new UploadResult(true, Id));

        var appended = await Append(Storage(connector)).RunAsync(Caller, Here, Id, "\nsécond");

        sent.Should().Equal(Encoded("first\nsécond"));
        appended.Result!.SizeBytes.Should().Be(Encoded("first\nsécond").Length);
    }

    [Theory]
    [InlineData("Storage is full", "Storage is full")]
    [InlineData(null, "The file could not be appended to.")]
    public async Task Append_UploadFails_SaysWhy(string? error, string detail)
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(false, Error: error));

        var appended = await Append(Holding("a").Storage).RunAsync(Caller, Here, Id, "b");

        appended.Should().BeEquivalentTo(new { Status = FileStatus.Invalid, Detail = detail }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Append_TwoAtOnce_BothLand()
    {
        _documents.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Doc("/notes/log.md"));
        string stored = "start";
        var connector = Substitute.For<IWritableConnector>();
        connector.ReadFileAsync("/notes/log.md", Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(stored)));
        var firstUploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int uploads = 0;
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(async c =>
        {
            string text = await new StreamReader(c.Arg<UploadRequest>().Content).ReadToEndAsync();
            if (Interlocked.Increment(ref uploads) == 1)
            {
                firstUploading.SetResult();
                await releaseFirst.Task;
            }
            stored = text;
            return new UploadResult(true, Id);
        });
        var append = Append(Storage(connector));

        // The first append is held mid-upload while the second starts. Every step before the lock
        // completes synchronously, so without the lock the second would read "start" right here.
        var first = Task.Run(() => append.RunAsync(Caller, Here, Id, "+a"));
        await firstUploading.Task;
        var second = append.RunAsync(Caller, Here, Id, "+b");
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        stored.Should().Be("start+a+b");
    }
}
