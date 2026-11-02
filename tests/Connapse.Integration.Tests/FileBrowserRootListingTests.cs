using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// #812: the Files tab showed only a few of a container's root files once many files had been uploaded into a
/// subfolder. The page listed the root by asking for every document under "/" with the store's default page of 50,
/// newest first, and only then kept the ones directly in the root, so older root files fell off the end of the page.
/// The reporter's layout is seeded as rows, oldest first: root files, a cost-data subfolder, then 50 files in tmt.
/// On the old page that showed both folders and no root files; with 47 subfolder files it showed the reporter's two.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileBrowserRootListingTests(SharedWebAppFixture fixture)
{
    private const int RootFiles = 14;
    private const int SubfolderFiles = 50;

    [Fact]
    public async Task RootListing_ManyFilesInSubfolderNewerThanRootFiles_ShowsEveryRootFile()
    {
        var rootNames = Enumerable.Range(1, RootFiles).Select(i => $"root-{i:D2}.md").ToList();
        var paths = rootNames.Select(n => $"/{n}")
            .Append("/cost-data/cost.md")
            .Concat(Enumerable.Range(1, SubfolderFiles).Select(i => $"/tmt/tmt-{i:D2}.md"));
        string containerId = await SeedAsync(paths);

        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.FindAll(".file-table, .empty-folder, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));

        var listed = page.FindAll("tr.file-row .entry-name").Select(n => n.TextContent.Trim()).ToList();
        listed.Should().Contain(rootNames, "every root file is in the container, so the root view lists it");
        listed.Should().Contain(["cost-data", "tmt"]);
    }

    /// <summary>A container holding a Ready document at each path, each created a second after the one before.</summary>
    private async Task<string> SeedAsync(IEnumerable<string> paths)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var container = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"root-{Guid.NewGuid():N}"[..20]));
        Guid containerId = Guid.Parse(container.Id);

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        DateTime createdAt = DateTime.UtcNow.AddHours(-1);
        foreach (string path in paths)
        {
            createdAt = createdAt.AddSeconds(1);
            ctx.Documents.Add(new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = containerId,
                FileName = path[(path.LastIndexOf('/') + 1)..],
                Path = path,
                ContentHash = "",
                CreatedAt = createdAt,
                IngestionStatus = DocumentStatus.Ready,
                Metadata = [],
            });
        }
        await ctx.SaveChangesAsync();
        return container.Id;
    }
}
