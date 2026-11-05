using System.Reflection;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using Connapse.Web.Components.Pages;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// CON-227: the create dialog offers "Agent memory" as a container type, a memory container's card says so instead of
/// counting files, and its page is the Memory tab alone, with no files, settings, upload or drop zone.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryContainerPagesTests : IDisposable
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitContext ctx = new();
    private readonly IContainerStore containers = ContainerStoreFakes.Create();
    private readonly IDocumentStore documents = Substitute.For<IDocumentStore>();
    private readonly IFolderStore folders = Substitute.For<IFolderStore>();

    public MemoryContainerPagesTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization().SetAuthorized("pat");
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        var memory = Substitute.For<IMemoryService>();
        memory.RecallAsync(default!, default!).ReturnsForAnyArgs(new MemoryRecall([]));
        memory.ListAsync(default, default!, default, default, default, default!).ReturnsForAnyArgs(new MemoryFactPage([], null));
        memory.CountAsync(default, default!, default!).ReturnsForAnyArgs(new MemoryFactCount(0, false));
        var models = Substitute.For<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance);
        models.GetModelsAsync(default, default).ReturnsForAnyArgs(new List<EmbeddingModelInfo>());
        documents.GetContainerStatsAsync(default, default).ReturnsForAnyArgs(new ContainerStats(0, 0, 0, 0, 0, 0, null));

        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(documents);
        ctx.Services.AddSingleton(folders);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(memory);
        ctx.Services.AddSingleton(models);
        ctx.Services.AddSingleton(new IngestionProgressNotifier());
        ctx.Services.AddOptions();
        ctx.Services.AddApplication();
        // The pages inject every container and file operation; the ones these tests don't reach get bare stand-ins.
        foreach (var port in typeof(IContainerStore).Assembly.GetTypes().Concat(typeof(IOperationAuthorizer).Assembly.GetTypes())
                     .Where(t => t is { IsInterface: true, IsGenericTypeDefinition: false, IsPublic: true }
                         && t.Namespace is "Connapse.Core.Interfaces" or "Connapse.Application.Ports"))
            ctx.Services.TryAddSingleton(port, _ => Substitute.For([port], []));
    }

    public void Dispose() => ctx.Dispose();

    private Container Seed(string name, ContainerKind kind, int files = 0)
    {
        var container = new Container(Guid.NewGuid().ToString(), name, null, Epoch, Epoch, files, Kind: kind);
        containers.GetAsync(Guid.Parse(container.Id), Arg.Any<CancellationToken>()).Returns(container);
        containers.GetByNameAsync(name, Arg.Any<CancellationToken>()).Returns(container);
        return container;
    }

    [Fact]
    public void CreateDialog_Created_MakesADocumentContainerWithNoTypeChoice()
    {
        containers.ListAsync(default, default, default).ReturnsForAnyArgs([]);
        containers.CreateAsync(default!, default).ReturnsForAnyArgs(c => new Container(Guid.NewGuid().ToString(),
            c.Arg<CreateContainerRequest>().Name, null, Epoch, Epoch, Kind: c.Arg<CreateContainerRequest>().Kind));
        var cut = ctx.Render<Home>();
        cut.Find(".container-list-header button").Click();

        cut.FindAll("[role=radio]").Should().BeEmpty("memory spaces are made on the Memory page, not here");
        cut.Markup.Should().NotContain("Agent memory");
        cut.Find("#container-kind-note").TextContent.Should().Contain("storage Connapse owns");
        cut.Find("input[placeholder='my-project']").Input("project-docs");
        cut.Find(".modal-footer-custom .btn-primary").Click();

        cut.WaitForAssertion(() => containers.Received(1).CreateAsync(
            new CreateContainerRequest("project-docs", null, ContainerKind.Documents), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Cards_MemoryContainer_SaysAgentMemoryInsteadOfCountingFiles()
    {
        List<Container> seeded = [Seed("docs", ContainerKind.Documents, files: 3), Seed("agent-notes", ContainerKind.Memory)];
        containers.ListAsync(default, default, default).ReturnsForAnyArgs(seeded);

        var cut = ctx.Render<Home>();

        cut.WaitForAssertion(() => cut.FindAll(".container-card").Should().HaveCount(2));
        var cards = cut.FindAll(".container-card");
        cards[0].QuerySelector(".container-card-meta")!.TextContent.Should().Contain("3 files");
        cards[1].QuerySelector(".container-card-meta")!.TextContent.Should().Contain("Agent memory").And.NotContain("files");
        cards[1].QuerySelector(".container-card-memory-icon").Should().NotBeNull();
    }

    [Fact]
    public void DeleteDialog_MemoryContainer_WarnsThatItsFactsGoToo()
    {
        List<Container> seeded = [Seed("docs", ContainerKind.Documents), Seed("agent-notes", ContainerKind.Memory)];
        containers.ListAsync(default, default, default).ReturnsForAnyArgs(seeded);
        var cut = ctx.Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".container-card").Should().HaveCount(2));

        cut.FindAll(".container-delete-btn")[0].Click();
        cut.FindAll("#delete-memory-warning").Should().BeEmpty();
        cut.Find(".modal-footer-custom .btn-outline-secondary").Click();
        cut.FindAll(".container-delete-btn")[1].Click();

        cut.Find("#delete-memory-warning").TextContent.Should().Contain("Every memory fact in it is deleted too");
    }

    [Fact]
    public void ContainerPage_MemoryContainer_IsTheMemoryTabAlone()
    {
        var memory = Seed("agent-notes", ContainerKind.Memory);

        var cut = ctx.Render<FileBrowser>(p => p.Add(b => b.ContainerId, memory.Id));

        cut.WaitForAssertion(() => cut.Find("#memory-empty"));
        cut.FindAll(".nav-tabs .nav-link").Select(t => t.TextContent.Trim()).Should().Equal("Memory");
        cut.Find("#memory-tab").ClassList.Should().Contain("active");
        cut.FindAll("#fileDropZone").Should().BeEmpty();
        cut.FindAll("#fileUploadInput").Should().BeEmpty();
        cut.Markup.Should().NotContain("New Folder").And.NotContain("data-stat=\"documents\"");
        cut.Find("#memory-container-note").TextContent.Should().Contain("Agent memory");
        folders.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void ContainerPage_DocumentsContainer_KeepsFilesFirstWithUpload()
    {
        var docs = Seed("docs", ContainerKind.Documents);

        var cut = ctx.Render<FileBrowser>(p => p.Add(b => b.ContainerId, docs.Id));

        cut.WaitForAssertion(() => cut.Find("#fileDropZone"));
        cut.FindAll(".nav-tabs .nav-link").Select(t => t.TextContent.Trim()).Should().Equal("Files", "Memory", "Settings");
        cut.Find("#fileUploadInput");
        cut.FindAll("#memory-container-note").Should().BeEmpty();
    }
}
