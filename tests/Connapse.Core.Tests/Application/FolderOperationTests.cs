using System.Security.Claims;
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
/// folders.create, folders.delete and batches.status: their rules over random inputs, each expectation worked out here
/// from the rule, and the fixed cases random inputs reach rarely. Every sample builds its own fakes, because CsCheck
/// runs samples in parallel.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class FolderOperationTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Here = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    private static Document Doc(string path, DocumentStatus status = DocumentStatus.Ready) =>
        new Document(Guid.NewGuid().ToString(), Here.ToString(), Path.GetFileName(path), "text/plain", path, 4, Epoch,
            new Dictionary<string, string> { ["ErrorMessage"] = "boom" }) with
        { Status = status };

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IFolderStore Folders = Substitute.For<IFolderStore>();
        public readonly IWritableConnector Files = Substitute.For<IWritableConnector>();
        public readonly IManagedStorageProvider Storage = Substitute.For<IManagedStorageProvider>();
        public readonly IIngestionQueue Queue = Substitute.For<IIngestionQueue>();
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly IIngestionJobs Jobs = Substitute.For<IIngestionJobs>();
        public readonly IDocumentAccess Access = Substitute.For<IDocumentAccess>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();

        public World(bool allowed = true, bool containerExists = true)
        {
            Storage.CreateConnector(Arg.Any<string>()).Returns(Files);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(allowed);
            Containers.GetAsync(Here, Arg.Any<CancellationToken>())
                .Returns(containerExists ? new Container(Here.ToString(), "here", null, Epoch, Epoch) : null);
            Access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        /// <summary>The documents under /docs/, each also found by id.</summary>
        public void Holds(IReadOnlyList<Document> docs)
        {
            Documents.ListAsync(Here, "/docs/", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(docs);
            foreach (var doc in docs)
                Documents.GetAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);
        }

        public bool Touched =>
            Documents.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "DeleteAsync")
            || Folders.ReceivedCalls().Any(c => c.GetMethodInfo().Name is "CreateAsync")
            || Queue.ReceivedCalls().Any() || Files.ReceivedCalls().Any() || Audit.ReceivedCalls().Any();

        public CreateFolder Create() => new(Containers, Folders, Audit, Authorizer);

        public DeleteFile DeleteFile() => new(Containers, Documents, Folders, Storage, Queue, Audit, Authorizer, NullLogger<DeleteFile>.Instance);

        public DeleteFolder Delete() => new(Containers, Documents, Folders, Queue, DeleteFile(), Audit, Authorizer);

        public GetBatchStatus Batch() => new(Jobs, Documents, Access, Authorizer);
    }

    /// <summary>A folder path as a caller might send it, and the path it names when it names one.</summary>
    public enum PathKind { Plain, Backslashes, Missing, Blank, Root, Traversal, TooDeep }

    private static string? PathFor(PathKind kind) => kind switch
    {
        PathKind.Plain => "docs",
        PathKind.Backslashes => "\\docs\\",
        PathKind.Missing => null,
        PathKind.Blank => "  ",
        PathKind.Root => "/",
        PathKind.Traversal => "docs/../secrets",
        _ => string.Join('/', Enumerable.Repeat("d", 51)),
    };

    private static bool Valid(PathKind kind) => kind is PathKind.Plain or PathKind.Backslashes;

    // ── folders.create ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(CreateFolder))]
    public Task Create_AnyRequest_CreatesOnlyANewValidFolderForAWriter() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<PathKind>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, kind, taken) = t;
                var world = new World(writer, exists);
                world.Folders.ExistsAsync(Here, "/docs/", Arg.Any<CancellationToken>()).Returns(taken);
                world.Folders.CreateAsync(Here, "/docs/", Arg.Any<CancellationToken>()).Returns(new Folder("f1", Here.ToString(), "/docs/", Epoch));

                var created = await new CreateFolder(world.Containers, world.Folders, world.Audit, world.Authorizer).RunAsync(caller, Here, PathFor(kind));

                FolderStatus expected = !writer ? FolderStatus.Forbidden
                    : !Valid(kind) ? FolderStatus.Invalid
                    : !exists ? FolderStatus.ContainerNotFound
                    : taken ? FolderStatus.Exists
                    : FolderStatus.Success;
                created.Status.Should().Be(expected);
                if (expected != FolderStatus.Success)
                {
                    world.Touched.Should().BeFalse();
                    return;
                }
                created.Result!.Path.Should().Be("/docs/");
                await world.Audit.Received(1).LogAsync("folder.created", "folder", "f1", Arg.Any<object>(), Arg.Any<CancellationToken>());
            }, iter: 1000);

    // ── folders.delete ────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(DeleteFolder))]
    public Task Delete_AnyFolder_RemovesItAndEverythingUnderItForAWriter() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Enum<PathKind>(), Gen.Bool, Gen.Int[0, 3], Gen.Bool)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, kind, recorded, count, storeDeletes) = t;
                var world = new World(writer, exists);
                var docs = Enumerable.Range(0, count).Select(i => Doc($"/docs/sub/{i}.md")).ToList();
                world.Holds(docs);
                world.Folders.ExistsAsync(Here, "/docs/", Arg.Any<CancellationToken>()).Returns(recorded);
                // The folder store says false when a delete elsewhere removed the folder first.
                world.Folders.DeleteAsync(Here, "/docs/", Arg.Any<CancellationToken>()).Returns(storeDeletes);

                var deleted = await new DeleteFolder(world.Containers, world.Documents, world.Folders, world.Queue, world.DeleteFile(),
                    world.Audit, world.Authorizer).RunAsync(caller, Here, PathFor(kind));

                FolderStatus expected = !writer ? FolderStatus.Forbidden
                    : !Valid(kind) ? FolderStatus.Invalid
                    : !exists ? FolderStatus.ContainerNotFound
                    : !recorded && count == 0 ? FolderStatus.NotFound
                    : recorded && !storeDeletes ? FolderStatus.NotFound
                    : FolderStatus.Success;
                deleted.Status.Should().Be(expected);
                if (expected != FolderStatus.Success)
                {
                    world.Files.ReceivedCalls().Should().BeEmpty();
                    world.Audit.ReceivedCalls().Should().BeEmpty();
                    world.Documents.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == "DeleteAsync");
                    return;
                }

                deleted.Result.Should().Be(new FolderDeletion("/docs/", count, StorageCleanupFailed: false));
                foreach (var doc in docs)
                {
                    await world.Queue.Received(1).CancelJobForDocumentAsync(doc.Id);
                    // A recorded folder's stored files are the folder store's to remove, under its lock.
                    await world.Files.Received(recorded ? 0 : 1).DeleteFileAsync(doc.Path, Arg.Any<CancellationToken>());
                }
                // A recorded folder's records go in one delete; an implied one's documents one at a time, as files.delete does.
                await world.Folders.Received(recorded ? 1 : 0).DeleteAsync(Here, "/docs/", Arg.Any<CancellationToken>());
                world.Documents.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "DeleteAsync").Should().Be(recorded ? 0 : count);
                await world.Audit.Received(1).LogAsync("folder.deleted", "folder", "/docs/", Arg.Any<object>(), Arg.Any<CancellationToken>());
            }, iter: 1000);

    // ── batches.status ────────────────────────────────────────────────────────────────────────────

    public enum JobKind { Known, Unknown, Blank }

    [Fact]
    [RandomInputTestOf(typeof(GetBatchStatus))]
    public Task Batch_AnyJob_AnswersItsDocumentsStateOnlyWhenReadable() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Enum<JobKind>(), Gen.Bool, Gen.Enum<DocumentStatus>())
            .SampleAsync(async t =>
            {
                var (caller, reader, kind, readable, status) = t;
                var world = new World(reader);
                var doc = Doc("/docs/a.md", status);
                world.Holds([doc]);
                world.Jobs.DocumentIdAsync("42", Arg.Any<CancellationToken>()).Returns(doc.Id);
                world.Access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), doc, Arg.Any<CancellationToken>()).Returns(readable);
                string jobId = kind switch { JobKind.Known => "42", JobKind.Unknown => "43", _ => " " };

                var read = await new GetBatchStatus(world.Jobs, world.Documents, world.Access, world.Authorizer).RunAsync(caller, jobId);

                BatchStatus expected = !reader ? BatchStatus.Forbidden
                    : kind == JobKind.Known && readable ? BatchStatus.Success
                    : BatchStatus.NotFound;
                read.Status.Should().Be(expected);
                if (expected != BatchStatus.Success)
                    return;

                string state = status switch
                {
                    DocumentStatus.Queued => "Queued",
                    DocumentStatus.Processing => "Processing",
                    DocumentStatus.Ready => "Completed",
                    _ => "Failed",
                };
                read.Result.Should().Be(new BatchProgress("42", doc.Id, Here.ToString(), state, status == DocumentStatus.Ready ? 100 : 0, "boom"));
            }, iter: 1000);

    // ── fixed cases ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refusals_SayWhatWasNotAllowed()
    {
        var world = new World(allowed: false);

        (await world.Create().RunAsync(Caller, Here, "docs")).Detail.Should().Be("You don't have permission to create folders.");
        (await world.Delete().RunAsync(Caller, Here, "docs")).Detail.Should().Be("You don't have permission to delete folders.");
        (await world.Batch().RunAsync(Caller, "42")).Detail.Should().Be("You don't have permission to view ingestion progress.");
    }

    [Theory]
    [InlineData(PathKind.Missing, "Folder path is required")]
    [InlineData(PathKind.Traversal, "Path must not contain '..' segments")]
    [InlineData(PathKind.TooDeep, "Path exceeds maximum depth of 50 levels")]
    [InlineData(PathKind.Root, "The root folder can't be created or deleted")]
    public async Task InvalidPath_SaysWhyAndNamesThePath(PathKind kind, string detail)
    {
        var created = await new World().Create().RunAsync(Caller, Here, PathFor(kind));

        created.Detail.Should().Be(detail);
        created.Field.Should().Be("path");
    }

    [Fact]
    public async Task Delete_ImpliedFoldersStoredFileThatWontGo_IsReportedNotFailed()
    {
        var world = new World();
        var doc = Doc("/docs/a.md");
        world.Holds([doc]);
        world.Files.DeleteFileAsync(doc.Path, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        var deleted = await world.Delete().RunAsync(Caller, Here, "docs");

        deleted.Result.Should().Be(new FolderDeletion("/docs/", 1, StorageCleanupFailed: true));
    }

    [Fact]
    public async Task Create_TakenFolder_SaysWhichPath()
    {
        var world = new World();
        world.Folders.ExistsAsync(Here, "/docs/", Arg.Any<CancellationToken>()).Returns(true);

        (await world.Create().RunAsync(Caller, Here, "docs")).Detail.Should().Be("Folder '/docs/' already exists");
    }
}
