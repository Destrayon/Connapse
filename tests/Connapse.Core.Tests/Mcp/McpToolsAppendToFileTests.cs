using System.Security.Claims;
using System.Text;
using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsAppendToFileTests
{
    private static readonly Guid ContainerId = Guid.Parse("7b1c9e2a-0000-4000-8000-0000000000aa");
    private const string DocId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string FilePath = "/log/2026-11-08.md";

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IUploadService _uploads = Substitute.For<IUploadService>();
    private readonly IManagedStorageProvider _storage = Substitute.For<IManagedStorageProvider>();

    public McpToolsAppendToFileTests()
    {
        _containers.GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(new Container(ContainerId.ToString(), "notes", null, DateTime.UtcNow, DateTime.UtcNow));
        var connector = Substitute.For<IWritableConnector>();
        connector.ReadFileAsync(FilePath, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("- a")));
        _storage.CreateConnector(Arg.Any<string>()).Returns(connector);
        _documents.GetByPathAsync(ContainerId, FilePath, Arg.Any<CancellationToken>()).Returns(
            new Document(DocId, ContainerId.ToString(), "2026-11-08.md", "text/markdown", FilePath, 3, DateTime.UtcNow, new() { ["Status"] = "Ready" }));
    }

    private Task<string> Append(string text, string? containerId = null)
    {
        var access = Substitute.For<IDocumentAccess>();
        access.CanReadAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<Document>(), Arg.Any<CancellationToken>()).Returns(true);
        var settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings());
        var append = new AppendToFile(_containers, _documents, access, _storage, _uploads, McpCaller.Allowed, settings);
        return McpTools.AppendToFile(append, new GetContainer(_containers, McpCaller.Allowed), McpCaller.Http,
            containerId ?? ContainerId.ToString(), FilePath, text);
    }

    [Fact]
    public async Task AppendToFile_Appended_SaysWhereItWentAndTheReindexJob()
    {
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, DocId, "job-7"));

        (await Append("\n- b")).Should().Be($"Appended to '{FilePath}' (ID: {DocId}); it is now 7 bytes. Reindexing (job job-7).");
    }

    [Fact]
    public async Task AppendToFile_NoJobQueued_LeavesTheJobOut()
    {
        _uploads.UploadAsync(Arg.Any<UploadRequest>(), Arg.Any<CancellationToken>()).Returns(new UploadResult(true, DocId));

        (await Append("b")).Should().Be($"Appended to '{FilePath}' (ID: {DocId}); it is now 4 bytes.");
    }

    [Fact]
    public async Task AppendToFile_NoText_ReturnsTheOperationsError() =>
        (await Append("")).Should().Be("Error: text is required");

    [Fact]
    public async Task AppendToFile_ContainerNotFound_ReturnsError() =>
        (await Append("b", Guid.NewGuid().ToString())).Should().StartWith("Error:").And.Contain("not found");
}
