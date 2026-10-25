using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The upload operations' fixed cases: the text each refusal gives, which is what MCP shows, and the edges random
/// inputs reach rarely: the bulk limit's boundary, what reaches the upload service, and the connector the
/// writability check opens.
/// </summary>
[Trait("Category", "Unit")]
public class UploadOperationTests
{
    private static readonly Guid Here = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");
    private static readonly Guid User = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000ee");
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly OperationContext Caller = new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, User.ToString())], "test")), OperationSurface.Rest);

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly IManagedStorageProvider _storage = Substitute.For<IManagedStorageProvider>();
    private readonly IFileTypeValidator _types = Substitute.For<IFileTypeValidator>();
    private readonly IUploadService _uploads = Substitute.For<IUploadService>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IWritableConnector _connector = Substitute.For<IWritableConnector, IDisposable>();

    public UploadOperationTests()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        _containers.GetAsync(Here, Arg.Any<CancellationToken>()).Returns(new Container(Here.ToString(), "here", null, Epoch, Epoch));
        _storage.CreateConnector(Here.ToString()).Returns(_connector);
        _types.IsSupported(Arg.Any<string>()).Returns(c => !c.Arg<string>().EndsWith(".xyz", StringComparison.Ordinal));
        _types.SupportedExtensions.Returns(new HashSet<string> { ".txt", ".md", ".docx" });
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, "doc-1", "job-1"));
    }

    private UploadFile Upload() => new(_containers, _storage, _types, _uploads, _authorizer);

    private BulkUploadFiles BulkUpload() => new(_containers, _storage, _types, _uploads, _authorizer);

    private static FileUpload File(string name = "a.md", string? path = null) => new(name, new MemoryStream(Encoding.UTF8.GetBytes("hi")), path);

    [Fact]
    public async Task Refusals_SayWhatWasNotAllowed()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(false);

        (await Upload().RunAsync(Caller, Here, File())).Detail.Should().Be("You don't have permission to upload files.");
        (await BulkUpload().RunAsync(Caller, Here, [File()])).Detail.Should().Be("You don't have permission to upload files.");
    }

    [Theory]
    [InlineData("a/b.md", null, "Invalid filename: 'a/b.md'.")]
    [InlineData("a.md", "/x/../y", "Path traversal is not allowed.")]
    [InlineData("a.xyz", null, "Unsupported file extension. Supported types: .docx, .md, .txt")]
    public async Task Upload_ARefusedFile_SaysWhy(string name, string? path, string detail)
    {
        var uploaded = await Upload().RunAsync(Caller, Here, File(name, path));

        uploaded.Detail.Should().Be(detail);
        _uploads.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_LongNameDeepPathOrEmpty_SaysWhichLimit()
    {
        (await Upload().RunAsync(Caller, Here, File(new string('a', 253) + ".md"))).Detail.Should().Be("Filename exceeds 255 characters.");
        (await Upload().RunAsync(Caller, Here, File(path: string.Join('/', Enumerable.Repeat("d", 51))))).Detail
            .Should().Be("Path exceeds maximum depth of 50 levels.");
        (await Upload().RunAsync(Caller, Here, new FileUpload("a.md", new MemoryStream()))).Detail
            .Should().Be("File is empty. Zero-byte uploads are not allowed.");
    }

    [Fact]
    public async Task Upload_MissingOrUnwritableContainer_SaysWhich()
    {
        var missing = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000cc");
        (await Upload().RunAsync(Caller, missing, File())).Detail.Should().Be($"Container {missing} not found");

        _storage.CreateConnector(Here.ToString()).Returns(Substitute.For<IConnector>());
        var refused = await Upload().RunAsync(Caller, Here, File());
        refused.Status.Should().Be(FileStatus.NotWritable);
        refused.Detail.Should().Be("This container is not writable.");
    }

    [Fact]
    public async Task Upload_SendsTheCallersIdTypeAndStrategy_AndDisposesTheCheckedConnector()
    {
        var uploaded = await Upload().RunAsync(Caller, Here, new FileUpload("a.md", new MemoryStream([1]), "docs", "text/markdown", "FixedSize"));

        uploaded.Result.Should().Be(new UploadedFile("doc-1", "job-1", "a.md", "/docs/a.md"));
        await _uploads.Received(1).UploadAsync(
            Arg.Is<UploadRequest>(r => r.UserId == User && r.ContentType == "text/markdown" && r.Strategy == "FixedSize" && r.IngestedVia == "API"),
            Arg.Any<CancellationToken>());
        ((IDisposable)_connector).Received(1).Dispose();
    }

    [Fact]
    public async Task Upload_StorageFailsWithoutSayingWhy_SaysTheFileCouldNotBeUploaded()
    {
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(false));

        (await Upload().RunAsync(Caller, Here, File())).Detail.Should().Be("The file could not be uploaded.");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task BulkUpload_AtMost100Files(int count, bool allowed)
    {
        _uploads.BulkUploadAsync(Arg.Any<BulkUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => new BulkUploadResult(0, 0, "b", [.. c.Arg<BulkUploadRequest>().Files.Select(_ => new UploadResult(true, "d", "j"))]));

        var uploaded = await BulkUpload().RunAsync(Caller, Here, [.. Enumerable.Range(0, count).Select(i => File($"f{i}.md"))]);

        uploaded.Succeeded.Should().Be(allowed);
        if (!allowed)
            uploaded.Detail.Should().Be("A bulk upload must name at most 100 files");
    }

    [Fact]
    public async Task BulkUpload_NoFiles_SaysOneIsRequired()
    {
        (await BulkUpload().RunAsync(Caller, Here, [])).Detail.Should().Be("At least one file is required");
    }

    [Fact]
    public async Task BulkUpload_AFileStorageFailsWithoutSayingWhy_IsAnUnknownError()
    {
        _uploads.BulkUploadAsync(Arg.Any<BulkUploadRequest>(), Arg.Any<CancellationToken>()).Returns(new BulkUploadResult(0, 1, "b", [new UploadResult(false)]));

        var uploaded = await BulkUpload().RunAsync(Caller, Here, [File()]);

        uploaded.Result!.Items.Should().ContainSingle().Which.Error.Should().Be("Unknown error");
        uploaded.Result.BatchId.Should().Be("b");
        uploaded.Result.UploadedCount.Should().Be(0);
    }
}
