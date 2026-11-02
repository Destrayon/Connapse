using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The upload operations' rules over random inputs: who is refused, which file is refused and why, that nothing is
/// stored on a refusal, and that a bulk upload sends only the files that pass and answers each in its place. Each
/// expectation is worked out here from the rule. Every sample builds its own fakes, because CsCheck runs samples in parallel.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class UploadRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Here = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");

    private static readonly Gen<OperationContext> AnyCaller =
        Gen.Enum<OperationSurface>().Select(s => new OperationContext(new ClaimsPrincipal(new ClaimsIdentity("test")), s));

    /// <summary>What a file to upload is like: one that passes, one each rule refuses, and one storage fails.</summary>
    public enum Kind { Good, TooLong, BadName, Traversal, TooDeep, Unsupported, Empty, StorageFails }

    private static readonly Gen<string?> AnyFolder = Gen.Int[0, 2].Select(i => i switch { 0 => null, 1 => "/docs", _ => (string?)"docs/sub/" });

    private static FileUpload File(Kind kind, string? folder, int n) => kind switch
    {
        Kind.TooLong => new(new string('a', ValidationConstants.MaxFileNameLength - 3) + ".txt", Bytes(), folder),
        Kind.BadName => new($"a{n}/b.txt", Bytes(), folder),
        Kind.Traversal => new($"f{n}.txt", Bytes(), "/docs/../up"),
        Kind.TooDeep => new($"f{n}.txt", Bytes(), string.Join('/', Enumerable.Repeat("d", ValidationConstants.MaxPathDepth + 1))),
        Kind.Unsupported => new($"f{n}.xyz", Bytes(), folder),
        Kind.Empty => new($"f{n}.txt", new MemoryStream(), folder),
        Kind.StorageFails => new($"fail{n}.txt", Bytes(), folder),
        _ => new($"f{n}.md", Bytes(), folder),
    };

    private static MemoryStream Bytes() => new(Encoding.UTF8.GetBytes("hello"));

    /// <summary>The refusal a rule gives a file before storage, or null when it passes them all.</summary>
    private static (FileStatus Status, string Field)? Refusal(Kind kind) => kind switch
    {
        Kind.TooLong or Kind.BadName => (FileStatus.Invalid, "fileName"),
        Kind.Traversal or Kind.TooDeep => (FileStatus.Invalid, "path"),
        Kind.Unsupported => (FileStatus.UnsupportedType, "fileName"),
        Kind.Empty => (FileStatus.Invalid, "content"),
        _ => null,
    };

    private sealed class World
    {
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IManagedStorageProvider Storage = Substitute.For<IManagedStorageProvider>();
        public readonly IFileTypeValidator Types = Substitute.For<IFileTypeValidator>();
        public readonly IUploadService Uploads = Substitute.For<IUploadService>();
        public readonly PolicyAuthorizer Authorizer;

        public World(bool writer, bool exists, bool writable)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.Writer && writer);
            Containers.GetAsync(Here, Arg.Any<CancellationToken>()).Returns(exists ? new Container(Here.ToString(), "here", null, Epoch, Epoch) : null);
            Storage.CreateConnector(Here.ToString()).Returns(_ => writable ? Substitute.For<IWritableConnector>() : Substitute.For<IConnector>());
            Types.IsSupported(Arg.Any<string>()).Returns(c => c.Arg<string>().EndsWith(".md", StringComparison.Ordinal) || c.Arg<string>().EndsWith(".txt", StringComparison.Ordinal));
            Types.SupportedExtensions.Returns(new HashSet<string> { ".txt", ".md" });
            Uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(c => Stored(c.Arg<UploadRequest>()));
            Uploads.BulkUploadAsync(Arg.Any<BulkUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(c =>
                {
                    var results = c.Arg<BulkUploadRequest>().Files.Select(Stored).ToList();
                    return new BulkUploadResult(results.Count(r => r.Success), results.Count(r => !r.Success), "batch-1", results);
                });
        }

        private static UploadResult Stored(UploadRequest r) => r.FileName.StartsWith("fail", StringComparison.Ordinal)
            ? new UploadResult(false, Error: "Upload failed: boom")
            : new UploadResult(true, $"doc-{r.FileName}", $"job-{r.FileName}");
    }

    private static string Via(OperationContext caller) => caller.Surface == OperationSurface.Mcp ? "MCP" : "API";

    private static string PathOf(FileUpload f) => PathUtilities.NormalizePath(PathUtilities.NormalizeFolderPath(f.Path ?? "/") + f.FileName);

    // ── files.upload ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(UploadFile))]
    public Task Upload_AnyFile_StoresOnlyWhatEveryRuleAllows() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<Kind>(), AnyFolder)
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, writable, kind, folder) = t;
                var world = new World(writer, exists, writable);
                var file = File(kind, folder, 0);

                var uploaded = await new UploadFile(world.Containers, world.Storage, world.Types, world.Uploads, world.Authorizer).RunAsync(caller, Here, file);

                var refusal = Refusal(kind);
                FileStatus expected = !writer ? FileStatus.Forbidden
                    : refusal is { } r ? r.Status
                    : !exists ? FileStatus.ContainerNotFound
                    : !writable ? FileStatus.NotWritable
                    : kind == Kind.StorageFails ? FileStatus.Invalid
                    : FileStatus.Success;
                uploaded.Status.Should().Be(expected);
                if (writer && refusal is { } refused)
                    uploaded.Field.Should().Be(refused.Field);
                if (expected is FileStatus.Success or FileStatus.Invalid && writer && refusal is null && exists && writable)
                {
                    await world.Uploads.Received(1).UploadAsync(
                        Arg.Is<UploadRequest>(q => q.ContainerId == Here && q.FileName == file.FileName && q.Path == file.Path && q.IngestedVia == Via(caller)),
                        Arg.Any<CancellationToken>());
                    if (expected == FileStatus.Success)
                        uploaded.Result.Should().Be(new UploadedFile($"doc-{file.FileName}", $"job-{file.FileName}", file.FileName, PathOf(file)));
                    else
                        uploaded.Detail.Should().Be("Upload failed: boom");
                }
                else
                    world.Uploads.ReceivedCalls().Should().BeEmpty();
            }, iter: 2000);

    // ── files.bulkUpload ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(BulkUploadFiles))]
    public Task BulkUpload_AnyFiles_SendsOnlyThePassingOnesAndAnswersEachInPlace() =>
        Gen.Select(AnyCaller, Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<Kind>().Array[0, 6], AnyFolder, Gen.Int[0, 9])
            .SampleAsync(async t =>
            {
                var (caller, writer, exists, writable, kinds, folder, roll) = t;
                // Now and then, more files than one call may name.
                if (roll == 0)
                    kinds = [.. Enumerable.Repeat(Kind.Good, BulkUploadFiles.MaxFiles + 1)];
                var world = new World(writer, exists, writable);
                var files = kinds.Select((k, i) => File(k, folder, i)).ToList();

                var uploaded = await new BulkUploadFiles(world.Containers, world.Storage, world.Types, world.Uploads, world.Authorizer).RunAsync(caller, Here, files);

                FileStatus expected = !writer ? FileStatus.Forbidden
                    : files.Count is 0 or > BulkUploadFiles.MaxFiles ? FileStatus.Invalid
                    : !exists ? FileStatus.ContainerNotFound
                    : !writable ? FileStatus.NotWritable
                    : FileStatus.Success;
                uploaded.Status.Should().Be(expected);
                if (expected == FileStatus.Invalid)
                    uploaded.Field.Should().Be("files");
                if (expected != FileStatus.Success)
                {
                    world.Uploads.ReceivedCalls().Should().BeEmpty();
                    return;
                }

                var items = uploaded.Result!.Items;
                items.Select(i => (i.FileName, i.Path)).Should().Equal(files.Select(f => (f.FileName, PathOf(f))));
                for (int i = 0; i < files.Count; i++)
                {
                    bool stored = Refusal(kinds[i]) is null && kinds[i] != Kind.StorageFails;
                    items[i].Uploaded.Should().Be(stored);
                    items[i].DocumentId.Should().Be(stored ? $"doc-{files[i].FileName}" : null);
                    if (kinds[i] == Kind.StorageFails)
                        items[i].Error.Should().Be("Upload failed: boom");
                }
                uploaded.Result.UploadedCount.Should().Be(items.Count(i => i.Uploaded));

                var passing = files.Where((_, i) => Refusal(kinds[i]) is null).Select(f => f.FileName).ToList();
                if (passing.Count == 0)
                    world.Uploads.ReceivedCalls().Should().BeEmpty();
                else
                    await world.Uploads.Received(1).BulkUploadAsync(
                        Arg.Is<BulkUploadRequest>(q => q.ContainerId == Here
                            && q.Files.Select(f => f.FileName).SequenceEqual(passing)
                            && q.Files.All(f => f.IngestedVia == Via(caller))),
                        Arg.Any<CancellationToken>());
            }, iter: 2000);
}
