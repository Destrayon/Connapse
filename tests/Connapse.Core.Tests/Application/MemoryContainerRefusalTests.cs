using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Code;
using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-227: an agent memory container holds memory facts only, so every document operation refuses it with the one
/// message that names the memory tools, and does no work on it. A documents container is unaffected.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryContainerRefusalTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Container Memory = new(Guid.NewGuid().ToString(), "agent-notes", null, Epoch, Epoch, Kind: ContainerKind.Memory);
    private static readonly Guid MemoryId = Guid.Parse(Memory.Id);

    private const string Message =
        "'agent-notes' is an agent memory container: it holds memory facts, not documents. "
        + "Use the memory tools (memory_remember, memory_recall) on it instead.";

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly IOperationAuthorizer _authorizer = new PolicyAuthorizer(_ => true);
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();

    public MemoryContainerRefusalTests()
    {
        _containers.GetAsync(MemoryId, Arg.Any<CancellationToken>()).Returns(Memory);
        _containers.GetByNameAsync(Memory.Name, Arg.Any<CancellationToken>()).Returns(Memory);
    }

    private static FileUpload File() => new("a.md", new MemoryStream(Encoding.UTF8.GetBytes("hi")));

    [Fact]
    public async Task UploadFile_MemoryContainer_IsRefusedAndStoresNothing()
    {
        var uploads = Substitute.For<IUploadService>();
        var storage = Substitute.For<IManagedStorageProvider>();
        var types = Substitute.For<IFileTypeValidator>();
        types.IsSupported(Arg.Any<string>()).Returns(true);

        var uploaded = await new UploadFile(_containers, storage, types, uploads, _authorizer).RunAsync(Caller, MemoryId, File());

        uploaded.Status.Should().Be(FileStatus.MemoryContainer);
        uploaded.Detail.Should().Be(Message);
        uploads.ReceivedCalls().Should().BeEmpty();
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BulkUploadFiles_MemoryContainer_IsRefusedAndStoresNothing()
    {
        var uploads = Substitute.For<IUploadService>();
        var storage = Substitute.For<IManagedStorageProvider>();
        var types = Substitute.For<IFileTypeValidator>();
        types.IsSupported(Arg.Any<string>()).Returns(true);

        var uploaded = await new BulkUploadFiles(_containers, storage, types, uploads, _authorizer).RunAsync(Caller, MemoryId, [File()]);

        uploaded.Status.Should().Be(FileStatus.MemoryContainer);
        uploaded.Detail.Should().Be(Message);
        uploads.ReceivedCalls().Should().BeEmpty();
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ListFiles_MemoryContainer_IsRefusedAndListsNothing()
    {
        var documents = Substitute.For<IDocumentStore>();
        var folders = Substitute.For<IFolderStore>();

        var listed = await new ListFiles(_containers, documents, folders, Substitute.For<IDocumentAccess>(), _authorizer)
            .RunAsync(Caller, MemoryId, new ListFilesQuery());

        listed.Status.Should().Be(FileStatus.MemoryContainer);
        listed.Detail.Should().Be(Message);
        documents.ReceivedCalls().Should().BeEmpty();
        folders.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateFolder_MemoryContainer_IsRefusedAndCreatesNothing()
    {
        var folders = Substitute.For<IFolderStore>();

        var created = await new CreateFolder(_containers, folders, _audit, _authorizer).RunAsync(Caller, MemoryId, "/notes");

        created.Status.Should().Be(FolderStatus.MemoryContainer);
        created.Detail.Should().Be(Message);
        folders.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ReindexContainer_MemoryContainer_IsRefusedAndReindexesNothing()
    {
        var reindex = Substitute.For<IReindexService>();

        var ran = await new ReindexContainer(_containers, reindex, _authorizer).RunAsync(Caller, MemoryId, new ContainerReindexQuery());

        ran.Status.Should().Be(ContainerStatus.MemoryContainer);
        ran.Detail.Should().Be(Message);
        reindex.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RetryFailedContainerDocuments_MemoryContainer_IsRefusedAndRetriesNothing()
    {
        var reindex = Substitute.For<IReindexService>();
        var scopes = new ServiceCollection().AddSingleton(reindex).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var retried = await new RetryFailedContainerDocuments(_containers, scopes, _audit, _authorizer).RunAsync(Caller, MemoryId);

        retried.Status.Should().Be(ContainerStatus.MemoryContainer);
        retried.Detail.Should().Be(Message);
        reindex.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileContainer_MemoryContainer_IsRefusedAndListsNoStorage()
    {
        var storage = Substitute.For<IManagedStorageProvider>();
        var documents = Substitute.For<IDocumentStore>();

        var synced = await new ReconcileContainer(_containers, storage, documents, Substitute.For<IStoredFileRemovalStore>(),
            Substitute.For<IIngestionQueue>(), Substitute.For<IOptionsMonitor<ChunkingSettings>>(), _audit, _authorizer).RunAsync(Caller, MemoryId);

        synced.Status.Should().Be(ContainerStatus.MemoryContainer);
        synced.Detail.Should().Be(Message);
        storage.ReceivedCalls().Should().BeEmpty();
        documents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateContainerSettings_MemoryContainer_IsRefusedAndSavesNothing()
    {
        var saved = await new UpdateContainerSettings(_containers, _audit, _authorizer)
            .RunAsync(Caller, MemoryId, new ContainerSettingsOverrides());

        saved.Status.Should().Be(ContainerStatus.MemoryContainer);
        saved.Detail.Should().Be(Message);
        await _containers.DidNotReceiveWithAnyArgs().UpdateSettingsOverridesAsync(default, default!);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    private SearchKnowledge Search(IKnowledgeSearch search)
    {
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings());
        return new SearchKnowledge(_containers, Substitute.For<ISourceStore>(), Substitute.For<ISourceVisibility>(), search, settings,
            new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchKnowledge_MemoryContainerByIdOrName_IsRefusedAndSearchesNothing(bool byId)
    {
        var search = Substitute.For<IKnowledgeSearch>();

        var searched = await Search(search).RunAsync(Caller, new SearchScope(byId ? Memory.Id : Memory.Name), new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.MemoryContainer);
        searched.Detail.Should().Be(Message);
        search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchContainer_MemoryContainer_IsRefused()
    {
        var search = Substitute.For<IKnowledgeSearch>();

        var searched = await new SearchContainer(Search(search)).RunAsync(Caller, MemoryId, new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.MemoryContainer);
        search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchKnowledge_DocumentsContainer_StillSearches()
    {
        var docs = new Container(Guid.NewGuid().ToString(), "docs", null, Epoch, Epoch);
        _containers.GetAsync(Guid.Parse(docs.Id), Arg.Any<CancellationToken>()).Returns(docs);
        var search = Substitute.For<IKnowledgeSearch>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult([new SearchHit("c", "d", "text", 0.5f, [])], 1, TimeSpan.Zero));

        var searched = await Search(search).RunAsync(Caller, new SearchScope(docs.Id), new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.Success);
    }

    [Fact]
    public async Task MapCodeRepository_MemoryContainer_IsRefusedAndAsksNothing()
    {
        var code = Substitute.For<ICodeTools>();

        var mapped = await new MapCodeRepository(Search(Substitute.For<IKnowledgeSearch>()), new PolicyAuthorizer(_ => true), code)
            .RunAsync(Caller, Memory.Name, null, 10);

        mapped.Status.Should().Be(CodeStatus.MemoryContainer);
        mapped.Detail.Should().Be(Message);
        code.ReceivedCalls().Should().BeEmpty();
    }
}
