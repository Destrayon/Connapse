using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The MCP contract for sources: discoverable and searchable, never enumerable.
/// <para>
/// The design spec holds two things at once — sources are listed alongside containers so
/// existing agent prompts keep working, *and* no MCP tool returns a file listing for one.
/// Satisfying the first without the second is the permissions leak epic #348 exists to close,
/// so the refusal tests below matter more than the positive ones.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpSourceKindTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Source> SeedSourceAsync(IServiceProvider sp, string? name = null)
    {
        var connections = sp.GetRequiredService<IConnectionStore>();
        var sources = sp.GetRequiredService<ISourceStore>();

        var connection = await connections.CreateAsync(
            new CreateConnectionRequest(ShortName("conn"), ConnectionProvider.S3, """{"region":"us-east-1"}"""),
            createdByUserId: null);

        return await sources.CreateAsync(
            new CreateSourceRequest(name ?? ShortName("src"), connection.Id, """{"bucketName":"b"}"""));
    }

    private async Task<Container> SeedContainerAsync(IServiceProvider sp)
    {
        var containers = sp.GetRequiredService<IContainerStore>();
        return await containers.CreateAsync(
            new CreateContainerRequest(ShortName("cnt"), null, ConnectorType.ManagedStorage, null));
    }

    // ── Discoverable ──────────────────────────────────────────────────────

    [Fact]
    public async Task ContainerList_IncludesSourcesTaggedWithTheirKind()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);
        var container = await SeedContainerAsync(scope.ServiceProvider);

        string output = await McpTools.ContainerList(scope.ServiceProvider);

        output.Should().Contain($"{source.Name} [source]");
        output.Should().Contain($"{container.Name} [managed]",
            "containers must keep appearing, and the kind is what lets an agent tell them apart");
    }

    [Fact]
    public async Task ContainerDescribe_AcceptsASourceAndSaysItHasNoFileListing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        string output = await McpTools.ContainerDescribe(scope.ServiceProvider, source.Id.ToString());

        output.Should().Contain($"Source: {source.Name}");
        output.Should().Contain("Kind: source");
        output.Should().Contain("list_files",
            "an agent should learn the tool does not apply here rather than spending a call finding out");
    }

    [Fact]
    public async Task ContainerDescribe_ResolvesASourceByName()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        string output = await McpTools.ContainerDescribe(scope.ServiceProvider, source.Name);

        output.Should().Contain("Kind: source");
    }

    [Fact]
    public async Task SearchKnowledge_AcceptsASourceId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        // Keyword mode deliberately: the default Hybrid path embeds the query, which would
        // make this a test of whether Ollama happens to be running rather than of whether a
        // source id resolves.
        string output = await McpTools.SearchKnowledge(
            scope.ServiceProvider, "anything", source.Id.ToString(), mode: "Keyword");

        // The corpus is empty, so this asserts the scope resolved rather than that results
        // came back — "not found" would mean the resolver rejected the source outright.
        output.Should().NotContain("not found");
    }

    // ── Not enumerable ────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_WithASourceId_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        // The single most important assertion in this file. list_files routes through
        // ResolveContainerIdAsync, which never resolves a source; teaching that method about
        // sources to make search work would silently turn this into a file-enumeration route
        // over somebody else's S3 bucket.
        string output = await McpTools.ListFiles(scope.ServiceProvider, source.Id.ToString());

        output.Should().Contain("not found",
            "a source must be refused outright, not returned as an empty listing");
    }

    [Fact]
    public async Task ListFiles_WithASourceName_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        string output = await McpTools.ListFiles(scope.ServiceProvider, source.Name);

        output.Should().Contain("not found");
    }

    [Fact]
    public async Task ContainerDelete_WithASourceId_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        string output = await McpTools.ContainerDelete(scope.ServiceProvider, source.Id.ToString());

        output.Should().Contain("not found");

        await using var context = await dbFactory.CreateDbContextAsync();
        (await context.Sources.AnyAsync(s => s.Id == source.Id))
            .Should().BeTrue("the source must survive an attempt to delete it as a container");
    }

    [Fact]
    public async Task GetDocument_WithASourceId_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        string output = await McpTools.GetDocument(
            scope.ServiceProvider, source.Id.ToString(), fileId: "/anything.md");

        output.Should().Contain("not found");
    }

    [Fact]
    public async Task ContainerStats_WithASourceId_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var source = await SeedSourceAsync(scope.ServiceProvider);

        string output = await McpTools.ContainerStats(scope.ServiceProvider, source.Id.ToString());

        output.Should().Contain("not found");
    }
}
