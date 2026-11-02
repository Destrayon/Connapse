using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// #812: the Files tab showed only a few of a container's root files once many files had been uploaded into a
/// subfolder. The page listed the root by asking for every document under "/" with the store's default page of 50,
/// newest first, and only then kept the ones directly in the root, so older root files fell off the end of the page.
/// The reporter's steps are followed here: root files, a subfolder, a failed bulk upload into it, then one-by-one uploads.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileBrowserRootListingTests(SharedWebAppFixture fixture)
{
    private const int RootFiles = 14;

    // 47 left the old page showing two folders and the two newest root files, as the reporter saw; 50 left none.
    [Theory]
    [InlineData(47)]
    [InlineData(50)]
    public async Task RootListing_ManyUploadsIntoSubfolderAfterRootFiles_ShowsEveryRootFile(int subfolderFiles)
    {
        string containerId = await SeedContainerAsync();
        var rootNames = Enumerable.Range(1, RootFiles).Select(i => $"root-{i:D2}.md").ToList();
        foreach (string name in rootNames)
            await UploadAsync(containerId, "/", (name, $"Root file {name}."));
        await UploadAsync(containerId, "/cost-data/", ("cost.md", "Cost data."));

        // The reporter's bulk upload into the subfolder failed, as this one does: one file isn't a document and the other is empty.
        using (var failed = Form("/tmt/", ("bulk.exe", "not a document"), ("bulk.md", "")))
            await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", failed);
        for (int i = 1; i <= subfolderFiles; i++)
            await UploadAsync(containerId, "/tmt/", ($"tmt-{i:D2}.md", $"Subfolder file {i}."));

        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.FindAll(".file-table, .empty-folder, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));

        var listed = page.FindAll("tr.file-row .entry-name").Select(n => n.TextContent.Trim()).ToList();
        listed.Should().Contain(rootNames, "every root file is in the database and storage, so the root view lists it");
        listed.Should().Contain(["cost-data", "tmt"]);
    }

    private async Task<string> SeedContainerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"root-{Guid.NewGuid():N}"[..20]));
        return container.Id;
    }

    private async Task UploadAsync(string containerId, string path, (string Name, string Text) file)
    {
        using var form = Form(path, file);
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
    }

    private static MultipartFormDataContent Form(string path, params (string Name, string Text)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, text) in files)
        {
            var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(part, "files", name);
        }
        form.Add(new StringContent(path), "path");
        return form;
    }
}
