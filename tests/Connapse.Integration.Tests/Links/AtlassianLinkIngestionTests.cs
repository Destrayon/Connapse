using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>
/// Jira issues' and Confluence pages' links reach the link layer as the pipeline ingests them (CON-34): written with
/// their chunks, a page standing for its id and its title, and dropped when a reindex reports them empty.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AtlassianLinkIngestionTests(SharedWebAppFixture fixture)
{
    private async Task<(Guid Container, Guid Document)> SeedAsync(IServiceProvider sp)
    {
        var dbFactory = sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid containerId = Guid.NewGuid(), documentId = Guid.NewGuid();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        ctx.Containers.Add(new ContainerEntity
        {
            Id = containerId,
            Name = $"atl-{containerId:N}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        ctx.Documents.Add(new DocumentEntity
        {
            Id = documentId,
            ContainerId = containerId,
            FileName = "100.md",
            Path = "/100.md",
            ContentHash = "",
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        });
        await ctx.SaveChangesAsync();
        return (containerId, documentId);
    }

    private static Task IngestAsync(IServiceProvider sp, Guid container, Guid document, Dictionary<string, string> metadata) =>
        sp.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream("# FLINK > Checkpoints stall\n\nThe barrier never arrives."u8.ToArray()),
            new IngestionOptions(
                DocumentId: document.ToString(), FileName: "100.md", ContentType: "text/markdown",
                ContainerId: container.ToString(), Path: "/100.md", Strategy: ChunkingStrategy.Record,
                Metadata: metadata));

    private static async Task<List<(string Type, string To)>> EdgesAsync(IServiceProvider sp, Guid document)
    {
        await using var ctx = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        string id = document.ToString();
        return (await ctx.LinkEdges.AsNoTracking().Where(e => e.DocumentId == id).ToListAsync())
            .Select(e => (e.Type, e.ToNode)).OrderBy(e => e.ToNode, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, string> Issue(string site, string json) =>
        JiraLinks.Metadata(site, JsonDocument.Parse(json).RootElement);

    [Fact]
    public async Task Ingest_JiraIssue_WritesItsLinks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        string site = $"s{Guid.NewGuid():N}";
        (Guid container, Guid document) = await SeedAsync(sp);

        await IngestAsync(sp, container, document, Issue(site, """
            {"key":"FLINK-5","fields":{"issuelinks":[{"type":{"name":"Blocks"},"outwardIssue":{"key":"FLINK-9"}}],
              "fixVersions":[{"name":"1.17.0"}]}}
            """));

        (await EdgesAsync(sp, document)).Should().Equal(
            ("blocks", $"jira:{site}:FLINK-9"), ("fix-version", $"jiraversion:{site}:FLINK/1.17.0"));
        (await sp.GetRequiredService<ILinkIndex>().NodesOfAsync([document.ToString()]))[document.ToString()]
            .Should().Equal($"jira:{site}:FLINK-5");
    }

    [Fact]
    public async Task Ingest_ConfluencePage_StandsForItsIdAndTitle()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        string site = $"s{Guid.NewGuid():N}";
        (Guid container, Guid document) = await SeedAsync(sp);

        await IngestAsync(sp, container, document,
            ConfluenceLinks.Metadata(site, "FLINK", "100", "FLIP-1 Design", "42", ["accepted"], "<p>x</p>"));

        (await EdgesAsync(sp, document)).Should().Equal(
            ("parent", $"confluence:{site}:42"), ("labelled", $"confluencelabel:{site}:accepted"));
        (await sp.GetRequiredService<ILinkIndex>().NodesOfAsync([document.ToString()]))[document.ToString()]
            .Should().BeEquivalentTo($"confluence:{site}:100", $"confluence:{site}:FLINK/FLIP-1 Design");
    }

    [Fact]
    public async Task Ingest_AgainWithNoLinks_RemovesTheOldOnes()
    {
        // A read reports every key, empty when the issue has dropped its links, so the reindex clears them.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        string site = $"s{Guid.NewGuid():N}";
        (Guid container, Guid document) = await SeedAsync(sp);
        await IngestAsync(sp, container, document, Issue(site, """
            {"key":"FLINK-5","fields":{"issuelinks":[{"type":{"name":"Blocks"},"outwardIssue":{"key":"FLINK-9"}}]}}
            """));

        await IngestAsync(sp, container, document, Issue(site, """{"key":"FLINK-5","fields":{}}"""));

        (await EdgesAsync(sp, document)).Should().BeEmpty();
    }
}
