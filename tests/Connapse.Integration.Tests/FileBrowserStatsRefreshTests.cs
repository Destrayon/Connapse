using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Web.Components.Pages;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-221: on a fresh install the container header kept saying "0 documents / 0 ready" after an upload finished,
/// while the file's own row showed Ready. The page read the header counts once on load and again only after a delete,
/// so neither the upload result nor the ingestion status change reached them until a manual refresh.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileBrowserStatsRefreshTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task OnStatusChanged_ListedFileBecomesReady_HeaderCountsIt()
    {
        string containerId = await CreateContainerAsync();
        Guid documentId = await AddDocumentAsync(containerId, "/notes.md", DocumentStatus.Queued);

        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.Find("[data-stat=ready]").TextContent.Should().Be("0 ready"), TimeSpan.FromSeconds(10));

        await SetStatusAsync(documentId, DocumentStatus.Ready);
        fixture.Factory.Services.GetRequiredService<IngestionProgressNotifier>()
            .NotifyStatusChanged(new DocumentStatusChangedEvent(documentId.ToString(), DocumentStatus.Ready));

        page.WaitForAssertion(() =>
        {
            page.Find("[data-stat=documents]").TextContent.Should().Be("1 document");
            page.Find("[data-stat=ready]").TextContent.Should().Be("1 ready");
            page.Find("[data-stat=processing]").TextContent.Should().Be("0 processing");
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HandleDropUploadResult_FileUploadedIntoEmptyContainer_HeaderCountsIt()
    {
        string containerId = await CreateContainerAsync();

        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.Find("[data-stat=documents]").TextContent.Should().Be("0 documents"), TimeSpan.FromSeconds(10));

        // The upload endpoint has stored the document by the time the drop script reports its result.
        Guid documentId = await AddDocumentAsync(containerId, "/report.md", DocumentStatus.Ready);
        string result = $$"""
            {"batchId":null,"totalCount":1,"successCount":1,
             "documents":[{"documentId":"{{documentId}}","jobId":null,"fileName":"report.md","sizeBytes":12,"path":"/report.md","error":null}]}
            """;
        await page.InvokeAsync(() => page.Instance.HandleDropUploadResult(result));

        page.WaitForAssertion(() =>
        {
            page.Find("[data-stat=documents]").TextContent.Should().Be("1 document");
            page.Find("[data-stat=ready]").TextContent.Should().Be("1 ready");
        }, TimeSpan.FromSeconds(10));
    }

    private async Task<string> CreateContainerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var container = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"stats-{Guid.NewGuid():N}"[..20]));
        return container.Id;
    }

    private async Task<Guid> AddDocumentAsync(string containerId, string path, DocumentStatus status)
    {
        var id = Guid.NewGuid();
        await WithContextAsync(ctx => ctx.Documents.AddAsync(new DocumentEntity
        {
            Id = id,
            ContainerId = Guid.Parse(containerId),
            FileName = path[(path.LastIndexOf('/') + 1)..],
            Path = path,
            ContentHash = "",
            CreatedAt = DateTime.UtcNow,
            IngestionStatus = status,
            Metadata = [],
        }).AsTask());
        return id;
    }

    private async Task SetStatusAsync(Guid documentId, DocumentStatus status)
    {
        await WithContextAsync(async ctx => (await ctx.Documents.SingleAsync(d => d.Id == documentId)).IngestionStatus = status);
    }

    private async Task WithContextAsync(Func<KnowledgeDbContext, Task> write)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await using var ctx = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        await write(ctx);
        await ctx.SaveChangesAsync();
    }
}
