using System.Security.Claims;
using System.Text;
using Connapse.Application;
using Connapse.Application.Code;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Application.Search;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-300: a tables container holds tables, which are never chunked or embedded, so text search never searches it and
/// text ingestion never adds a document to it. Each refusal does no work and says why.
/// </summary>
[Trait("Category", "Unit")]
public class TablesContainerRefusalTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Container Tables = new(Guid.NewGuid().ToString(), "sales", null, Epoch, Epoch, Kind: ContainerKind.Tables);
    private static readonly Guid TablesId = Guid.Parse(Tables.Id);

    private const string Message =
        "'sales' is a tables container: it holds tables, not documents, so text search doesn't search it "
        + "and documents can't be added to it.";

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();
    private readonly ISourceStore _sources = Substitute.For<ISourceStore>();
    private readonly ISourceVisibility _visibility = Substitute.For<ISourceVisibility>();

    public TablesContainerRefusalTests()
    {
        _containers.GetAsync(TablesId, Arg.Any<CancellationToken>()).Returns(Tables);
        _containers.GetByNameAsync(Tables.Name, Arg.Any<CancellationToken>()).Returns(Tables);
        _visibility.ForAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<CancellationToken>()).Returns(_ => true);
    }

    private SearchKnowledge Search(IKnowledgeSearch search)
    {
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings());
        return new SearchKnowledge(_containers, _sources, _visibility, search, settings,
            new PolicyAuthorizer(policy => policy == Policies.KnowledgeRead));
    }

    private static IOptionsMonitor<UploadSettings> UploadLimit()
    {
        IOptionsMonitor<UploadSettings> settings = Substitute.For<IOptionsMonitor<UploadSettings>>();
        settings.CurrentValue.Returns(new UploadSettings());
        return settings;
    }

    private static FileUpload File() => new("a.md", new MemoryStream(Encoding.UTF8.GetBytes("hi")));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SearchKnowledge_TablesContainerByIdOrName_IsRefusedAndSearchesNothing(bool byId)
    {
        var search = Substitute.For<IKnowledgeSearch>();

        var searched = await Search(search).RunAsync(Caller, new SearchScope(byId ? Tables.Id : Tables.Name), new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.TablesContainer);
        searched.Detail.Should().Be(Message);
        search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchContainer_TablesContainer_IsRefused()
    {
        var search = Substitute.For<IKnowledgeSearch>();

        var searched = await new SearchContainer(Search(search)).RunAsync(Caller, TablesId, new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.TablesContainer);
        search.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SearchKnowledge_SourceSharingATablesContainersName_SearchesTheSource()
    {
        var source = new Source(Guid.NewGuid(), Tables.Name, null, null, "{}", Epoch, Epoch);
        _sources.FindByNameAsync(Tables.Name, Arg.Any<CancellationToken>()).Returns(source);
        var search = Substitute.For<IKnowledgeSearch>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult([], 0, TimeSpan.Zero));

        var searched = await Search(search).RunAsync(Caller, new SearchScope(Tables.Name), new SearchRequest("anything"));

        searched.Status.Should().Be(SearchStatus.Success, "a tables container has nothing to search, so it never hides a source");
        await search.Received(1).SearchAsync("anything", Arg.Is<SearchOptions>(o => o.ContainerId == source.Id.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MapCodeRepository_TablesContainer_IsRefusedAndAsksNothing()
    {
        var code = Substitute.For<ICodeTools>();

        var mapped = await new MapCodeRepository(Search(Substitute.For<IKnowledgeSearch>()), new PolicyAuthorizer(_ => true), code)
            .RunAsync(Caller, Tables.Name, null, 10);

        mapped.Status.Should().Be(CodeStatus.TablesContainer);
        mapped.Detail.Should().Be(Message);
        code.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UploadFile_TablesContainer_IsRefusedAndStoresNothing()
    {
        var uploads = Substitute.For<IUploadService>();
        var storage = Substitute.For<IManagedStorageProvider>();
        var types = Substitute.For<IFileTypeValidator>();
        types.IsSupported(Arg.Any<string>()).Returns(true);

        var uploaded = await new UploadFile(_containers, storage, types, uploads, new PolicyAuthorizer(_ => true), UploadLimit())
            .RunAsync(Caller, TablesId, File());

        uploaded.Status.Should().Be(FileStatus.TablesContainer);
        uploaded.Detail.Should().Be(Message);
        uploads.ReceivedCalls().Should().BeEmpty();
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task BulkUploadFiles_TablesContainer_IsRefusedAndStoresNothing()
    {
        var uploads = Substitute.For<IUploadService>();
        var storage = Substitute.For<IManagedStorageProvider>();
        var types = Substitute.For<IFileTypeValidator>();
        types.IsSupported(Arg.Any<string>()).Returns(true);

        var uploaded = await new BulkUploadFiles(_containers, storage, types, uploads, new PolicyAuthorizer(_ => true), UploadLimit())
            .RunAsync(Caller, TablesId, [File()]);

        uploaded.Status.Should().Be(FileStatus.TablesContainer);
        uploaded.Detail.Should().Be(Message);
        uploads.ReceivedCalls().Should().BeEmpty();
        storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task AppendToFile_TablesContainer_IsRefusedAndWritesNothing()
    {
        var uploads = Substitute.For<IUploadService>();
        var storage = Substitute.For<IManagedStorageProvider>();
        var documents = Substitute.For<IDocumentStore>();

        var appended = await new AppendToFile(_containers, documents, Substitute.For<IDocumentAccess>(), storage, uploads,
            new PolicyAuthorizer(_ => true), UploadLimit()).RunAsync(Caller, TablesId, "notes.md", "more");

        appended.Status.Should().Be(FileStatus.TablesContainer);
        appended.Detail.Should().Be(Message);
        uploads.ReceivedCalls().Should().BeEmpty();
        documents.ReceivedCalls().Should().BeEmpty();
    }
}
