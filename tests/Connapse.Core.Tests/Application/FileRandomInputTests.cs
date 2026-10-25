using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The file operations' rules over random inputs, as OperationRandomInputTests checks the others: who is refused,
/// what is never touched on a refusal, and what a success does. Each expectation is worked out here from the rule,
/// not by calling the code under test. Every sample builds its own fakes, because CsCheck runs samples in parallel.
/// </summary>
[Trait("Category", "Unit")]
public class FileRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Here = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");
    private static readonly Guid Elsewhere = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000bb");

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static Document Doc(string id, string path, Guid container, string status = "Ready") =>
        new(id, container.ToString(), Path.GetFileName(path), "text/plain", path, path.Length, Epoch,
            new Dictionary<string, string> { ["Status"] = status, ["ErrorMessage"] = "boom" });

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IFolderStore Folders = Substitute.For<IFolderStore>();
        public readonly IKnowledgeFileSystem Files = Substitute.For<IKnowledgeFileSystem>();
        public readonly IIngestionQueue Queue = Substitute.For<IIngestionQueue>();
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly IDocumentAccess Access = Substitute.For<IDocumentAccess>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();

        public World(bool reader, bool writer, bool containerExists)
        {
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Reader).Returns(reader);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Writer).Returns(writer);
            Containers.GetAsync(Here, Arg.Any<CancellationToken>())
                .Returns(containerExists ? new Container(Here.ToString(), "here", null, Epoch, Epoch) : null);
        }

        /// <summary>The store holds <paramref name="docs"/>, by id and by path in their own container.</summary>
        public void Holds(IEnumerable<Document> docs)
        {
            foreach (var doc in docs)
            {
                Documents.GetAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
                Documents.GetByPathAsync(Guid.Parse(doc.ContainerId), doc.Path, Arg.Any<CancellationToken>()).Returns(doc);
            }
        }

        public bool Touched =>
            Documents.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "DeleteAsync" or "StoreAsync")
            || Queue.ReceivedCalls().Any() || Files.ReceivedCalls().Any() || Audit.ReceivedCalls().Any();

        public DeleteFile Delete() => new(Containers, Documents, Folders, Files, Queue, Audit, Authorizer, NullLogger<DeleteFile>.Instance);
    }

    /// <summary>A document reference: this container's file by id or path, another container's, or none at all.</summary>
    public enum FileRef { OwnId, OwnPath, OtherContainersId, UnknownId, UnknownPath }

    private static string RefFor(FileRef kind, Document own, Document other) => kind switch
    {
        FileRef.OwnId => own.Id,
        FileRef.OwnPath => own.Path,
        FileRef.OtherContainersId => other.Id,
        FileRef.UnknownId => Guid.NewGuid().ToString(),
        _ => "/nowhere/missing.md",
    };

    private static readonly Document Own = Doc(Guid.NewGuid().ToString(), "/docs/notes.md", Here);
    private static readonly Document Other = Doc(Guid.NewGuid().ToString(), "/docs/notes.md", Elsewhere);

    // ── files.list ────────────────────────────────────────────────────────────────────────────────

    private static readonly Gen<string> Segment = Gen.OneOf(Gen.Const("a"), Gen.Const("B"), Gen.Const("c"), Gen.Const("b"));

    /// <summary>A document's path: directly in /docs/, a level or two below it, or outside it.</summary>
    private static readonly Gen<string> AnyPath = Gen.OneOf(
        Segment.Select(n => $"/docs/{n}.md"),
        Gen.Select(Segment, Segment, (d, n) => $"/docs/{d}/{n}.md"),
        Gen.Select(Segment, Segment, (d, n) => $"/docs/{d}/deep/{n}.md"),
        Segment.Select(n => $"/elsewhere/{n}.md"));

    [Fact]
    [RandomInputTestOf(typeof(ListFiles))]
    public Task List_AnyFolder_AnswersItsFoldersThenFilesByNamePaged() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Segment.Array[0, 3], AnyPath.Array[0, 6], Gen.Int[-1, 4], Gen.Int[-1, 205].Nullable(), Gen.Int[0, 63])
            .SampleAsync(async t =>
            {
                var (caller, reader, exists, folderNames, paths, skip, take, deniedBits) = t;
                var world = new World(reader, writer: false, exists);
                var folders = folderNames.Select(n => new Folder(Guid.NewGuid().ToString(), Here.ToString(), $"/docs/{n}/", Epoch)).ToList();
                // The store answers by prefix, as it does: the documents under /docs/.
                var docs = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => Doc(Guid.NewGuid().ToString(), p, Here)).ToList();
                world.Folders.ListAsync(Here, "/docs/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(folders);
                world.Documents.ListAsync(Here, "/docs/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(docs.Where(d => d.Path.StartsWith("/docs/", StringComparison.Ordinal)).ToList());
                // The caller may not read the documents whose bit is set; those are neither listed nor imply a folder.
                var denied = docs.Where((_, i) => (deniedBits >> i & 1) == 1).Select(d => d.Id).ToHashSet();
                world.Access.ReadableAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
                    .Returns(c => (IReadOnlySet<string>)c.Arg<IReadOnlyList<Document>>().Select(d => d.Id).Where(id => !denied.Contains(id)).ToHashSet());
                docs = [.. docs.Where(d => !denied.Contains(d.Id))];

                var listed = await new ListFiles(world.Containers, world.Documents, world.Folders, world.Access, world.Authorizer)
                    .RunAsync(caller, Here, new ListFilesQuery("docs", skip, take));

                FileStatus expected = !reader ? FileStatus.Forbidden
                    : skip < 0 || take is < 1 or > 200 ? FileStatus.InvalidPage
                    : !exists ? FileStatus.ContainerNotFound
                    : FileStatus.Success;
                listed.Status.Should().Be(expected);
                if (expected != FileStatus.Success)
                {
                    world.Documents.ReceivedCalls().Should().BeEmpty();
                    return;
                }

                // Folders: recorded ones and those a deeper document implies, once each, ignoring case.
                var folderEntries = new List<string>();
                foreach (string name in folderNames.Concat(docs.Where(d => d.Path.Count(c => c == '/') > 2 && d.Path.StartsWith("/docs/", StringComparison.Ordinal))
                             .Select(d => d.Path.Split('/')[2])))
                    if (!folderEntries.Contains(name, StringComparer.OrdinalIgnoreCase))
                        folderEntries.Add(name);
                var fileEntries = docs.Where(d => d.Path.StartsWith("/docs/", StringComparison.Ordinal) && d.Path.Count(c => c == '/') == 2).Select(d => d.FileName);
                var all = folderEntries.Order(StringComparer.OrdinalIgnoreCase).Select(n => (n, true))
                    .Concat(fileEntries.Order(StringComparer.OrdinalIgnoreCase).Select(n => (n, false))).ToList();

                var page = listed.Result!;
                page.TotalCount.Should().Be(all.Count);
                var expectedPage = all.Skip(skip).Take(take ?? int.MaxValue).ToList();
                page.Entries.Select(e => (e.Name, e.IsFolder)).Should().Equal(expectedPage);
                page.HasMore.Should().Be(skip + expectedPage.Count < all.Count);
                page.Entries.Where(e => !e.IsFolder).Should().OnlyContain(e => e.Document != null && e.Id == e.Document.Id);
            }, iter: 2000);

    // ── files.get ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(GetFile))]
    public Task Get_AnyReference_FindsOnlyThisContainersReadableFile() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<FileRef>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, reader, exists, kind, readable) = t;
                var world = new World(reader, writer: false, exists);
                world.Holds([Own, Other]);
                world.Access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(readable);

                var read = await new GetFile(world.Containers, world.Documents, world.Access, world.Authorizer).RunAsync(caller, Here, RefFor(kind, Own, Other));

                FileStatus expected = !reader ? FileStatus.Forbidden
                    : !exists ? FileStatus.ContainerNotFound
                    : kind is FileRef.OwnId or FileRef.OwnPath && readable ? FileStatus.Success
                    : FileStatus.NotFound;
                read.Status.Should().Be(expected);
                if (expected == FileStatus.Success)
                    read.Result.Should().Be(Own);
            }, iter: 2000);

    // ── files.content ─────────────────────────────────────────────────────────────────────────────

    public enum Storage { Reads, Missing, Denied }

    [Fact]
    [RandomInputTestOf(typeof(GetFileContent))]
    public Task Content_AnyFile_GivesItsTextOnlyOnceIngestedAndReadable() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<FileRef>(), Gen.OneOf(Gen.Const("Ready"), Gen.Const("Pending"), Gen.Const("Processing"), Gen.Const("Queued"), Gen.Const("Failed")),
                Gen.OneOf(Gen.Const(".md"), Gen.Const(".pdf"), Gen.Const(".html"), Gen.Const(".xyz")), Gen.Enum<Storage>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, reader, kind, status, extension, storage, readable) = t;
                var world = new World(reader, writer: false, containerExists: true);
                var own = Doc(Own.Id, $"/docs/notes{extension}", Here, status);
                var other = Doc(Other.Id, $"/docs/notes{extension}", Elsewhere, status);
                world.Holds([own, other]);
                world.Access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(readable);
                var connector = Substitute.For<IConnector>();
                var stored = connector.ReadFileAsync(own.Path, Arg.Any<CancellationToken>());
                _ = storage switch
                {
                    Storage.Missing => stored.ThrowsAsync(new FileNotFoundException()),
                    Storage.Denied => stored.ThrowsAsync(new UnauthorizedAccessException()),
                    _ => stored.Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("raw text"))),
                };
                var storageProvider = Substitute.For<IManagedStorageProvider>();
                storageProvider.CreateConnector(Here.ToString()).Returns(connector);
                // One parser, for PDF and HTML; .xyz has none and .md is text.
                var parser = Substitute.For<IDocumentParser>();
                parser.SupportedExtensions.Returns(new HashSet<string> { ".pdf", ".html" });
                parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ParsedDocument("parsed text", [], []));

                var read = await new GetFileContent(world.Containers, world.Documents, world.Access, storageProvider, [parser], world.Authorizer)
                    .RunAsync(caller, Here, RefFor(kind, own, other));

                FileStatus expected = !reader ? FileStatus.Forbidden
                    : kind is not (FileRef.OwnId or FileRef.OwnPath) || !readable ? FileStatus.NotFound
                    : status is "Pending" or "Processing" or "Queued" ? FileStatus.NotReady
                    : status == "Failed" ? FileStatus.IngestionFailed
                    : extension == ".xyz" ? FileStatus.NoParser
                    : storage != Storage.Reads ? FileStatus.NotReadable
                    : FileStatus.Success;
                read.Status.Should().Be(expected);
                if (expected == FileStatus.IngestionFailed)
                    read.Detail.Should().Contain("boom");
                if (expected == FileStatus.Success)
                {
                    read.Result!.Document.Should().Be(own);
                    read.Result.Content.Should().Be(extension == ".md" ? "raw text" : "parsed text");
                }
                if (expected is FileStatus.Forbidden or FileStatus.NotFound or FileStatus.NotReady or FileStatus.IngestionFailed or FileStatus.NoParser)
                    connector.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── files.delete ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(DeleteFile))]
    public Task Delete_AnyFile_RemovesOnlyThisContainersFileAndAudits() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<FileRef>().Where(k => k != FileRef.OwnPath), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, kind, storageFails) = t;
                var world = new World(reader: true, writer, exists);
                world.Holds([Own, Other]);
                if (storageFails)
                    world.Files.DeleteAsync(Own.Path, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk"));
                string fileId = RefFor(kind, Own, Other);

                var deleted = await new DeleteFile(world.Containers, world.Documents, world.Folders, world.Files, world.Queue, world.Audit, world.Authorizer,
                    NullLogger<DeleteFile>.Instance).RunAsync(caller, Here, fileId);

                FileStatus expected = !writer ? FileStatus.Forbidden
                    : !exists ? FileStatus.ContainerNotFound
                    : kind == FileRef.OwnId ? FileStatus.Success
                    : FileStatus.NotFound;
                deleted.Status.Should().Be(expected);
                if (expected != FileStatus.Success)
                {
                    world.Touched.Should().BeFalse();
                    return;
                }
                deleted.Result.Should().Be(new FileDeletion(Own, storageFails));
                await world.Queue.Received(1).CancelJobForDocumentAsync(Own.Id);
                await world.Documents.Received(1).DeleteAsync(Own.Id, Arg.Any<CancellationToken>());
                await world.Folders.Received(1).DeleteEmptyAncestorsAsync(Here, Own.Path, Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("doc.deleted", "document", Own.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── files.bulkDelete ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(BulkDeleteFiles))]
    public Task BulkDelete_AnyIds_DeletesEachOfThisContainersFilesAndReportsTheRest() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Bool.Array[0, 5], Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, ownFlags, tooMany) = t;
                var world = new World(reader: true, writer, exists);
                // Each id is this container's file or another container's; too many is over the limit.
                var own = ownFlags.Select((_, i) => Doc($"own-{i}", $"/docs/{i}.md", Here)).ToList();
                var ids = ownFlags.Select((mine, i) => mine ? own[i].Id : Other.Id).ToList();
                if (tooMany)
                    ids = [.. Enumerable.Range(0, 101).Select(i => $"id-{i}")];
                world.Holds([.. own, Other]);

                var deleted = await new BulkDeleteFiles(world.Containers, world.Delete(), world.Authorizer).RunAsync(caller, Here, ids);

                FileStatus expected = !writer ? FileStatus.Forbidden
                    : ids.Count is 0 or > 100 ? FileStatus.Invalid
                    : !exists ? FileStatus.ContainerNotFound
                    : FileStatus.Success;
                deleted.Status.Should().Be(expected);
                if (expected == FileStatus.Invalid)
                    deleted.Field.Should().Be("fileIds");
                if (expected != FileStatus.Success)
                {
                    world.Touched.Should().BeFalse();
                    return;
                }
                deleted.Result!.Select(i => (i.FileId, i.Deleted)).Should().Equal(ids.Select((id, i) => (id, ownFlags[i])));
                deleted.Result!.Where(i => !i.Deleted).Should().OnlyContain(i => i.Error == $"File '{i.FileId}' not found in this container.");
                await world.Documents.Received(ownFlags.Count(f => f)).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
                await world.Documents.DidNotReceive().DeleteAsync(Other.Id, Arg.Any<CancellationToken>());
            }, iter: 2000);

    // ── files.reindexCheck ────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CheckFileReindex))]
    public Task ReindexCheck_AnyFile_ChecksOnlyThisContainersFile() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<FileRef>().Where(k => k != FileRef.OwnPath), Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, reader, exists, kind, needs, readable) = t;
                var world = new World(reader, writer: false, exists);
                world.Holds([Own, Other]);
                world.Access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(readable);
                var reindex = Substitute.For<IReindexService>();
                reindex.CheckDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(c => new ReindexCheck(c.Arg<string>(), needs, needs ? ReindexReason.ContentChanged : ReindexReason.Unchanged));

                var checkedFile = await new CheckFileReindex(world.Containers, world.Documents, world.Access, reindex, world.Authorizer).RunAsync(caller, Here, RefFor(kind, Own, Other));

                FileStatus expected = !reader ? FileStatus.Forbidden
                    : !exists ? FileStatus.ContainerNotFound
                    : kind == FileRef.OwnId && readable ? FileStatus.Success
                    : FileStatus.NotFound;
                checkedFile.Status.Should().Be(expected);
                if (expected == FileStatus.Success)
                    checkedFile.Result.Should().Be(new ReindexCheck(Own.Id, needs, needs ? ReindexReason.ContentChanged : ReindexReason.Unchanged));
                else
                    reindex.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── files.retry ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(RetryFile))]
    public Task Retry_AnyFile_QueuesOnlyThisContainersFileAndAudits() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<FileRef>().Where(k => k != FileRef.OwnPath), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, kind, queued) = t;
                var world = new World(reader: true, writer, exists);
                world.Holds([Own, Other]);
                world.Queue.EnqueueAsync(Arg.Any<IngestionJob>(), Arg.Any<CancellationToken>()).Returns(queued ? "job-1" : null);

                var retried = await new RetryFile(world.Containers, world.Documents, world.Queue, world.Audit, world.Authorizer).RunAsync(caller, Here, RefFor(kind, Own, Other));

                FileStatus expected = !writer ? FileStatus.Forbidden
                    : !exists ? FileStatus.ContainerNotFound
                    : kind == FileRef.OwnId ? FileStatus.Success
                    : FileStatus.NotFound;
                retried.Status.Should().Be(expected);
                if (expected != FileStatus.Success)
                {
                    world.Touched.Should().BeFalse();
                    return;
                }
                retried.Result.Should().Be(new FileRetry(Own.Id, queued ? "job-1" : null));
                // A fresh attempt budget, for this document exactly as it is stored.
                await world.Queue.Received(1).EnqueueAsync(Arg.Is<IngestionJob>(j =>
                    j.DocumentId == Own.Id && j.ResetAttempts && j.Options.Path == Own.Path && j.Options.FileName == Own.FileName
                    && j.Options.ContainerId == Here.ToString() && j.Options.ContentType == Own.ContentType), Arg.Any<CancellationToken>());
                await world.Audit.Received(1).LogAsync("doc.retried", "document", Own.Id, Arg.Any<object?>(), Arg.Any<CancellationToken>());
            }, iter: 2000);
}
