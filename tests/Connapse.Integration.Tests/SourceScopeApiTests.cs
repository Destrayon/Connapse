using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Proof tests for #859 (CON-45): the REST API takes a source's scope as a typed <c>scope</c>, stored
/// with the keys the connectors read, and publishes each provider's scope shape.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceScopeApiTests(SharedWebAppFixture fixture)
{
    private HttpClient Admin => fixture.AdminClient;

    [Fact]
    public async Task CreateSource_WithTypedScope_StoresTheConnectorsKeys()
    {
        Guid connectionId = await SeedConnectionAsync(ConnectionProvider.S3, """{"region":"us-east-1"}""");

        // "type" last, as a client writing its members in any order would send it.
        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("typed"),
            connectionId,
            scope = new { bucketName = "b", prefix = "team/", type = "s3" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        StoredScopeShouldBe(await SourceOf(response), """{"bucketName":"b","prefix":"team/"}""");
    }

    [Fact]
    public async Task CreateSource_WithScopeAndScopeJson_IsRejected()
    {
        Guid connectionId = await SeedConnectionAsync(ConnectionProvider.S3, """{"region":"us-east-1"}""");

        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("both"),
            connectionId,
            scope = new { type = "s3", bucketName = "b" },
            scopeJson = """{"bucketName":"b"}""",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("scope");
    }

    [Fact]
    public async Task CreateSource_WithAnotherProvidersScope_IsRejected()
    {
        Guid connectionId = await SeedConnectionAsync(ConnectionProvider.S3, """{"region":"us-east-1"}""");

        var response = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("wrong"),
            connectionId,
            scope = new { type = "filesystem", subPath = "notes" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("field").GetString().Should().Be("scope");
        body.GetProperty("error").GetString().Should().Contain("S3");
    }

    [Theory]
    [InlineData("""{"name":"n","scope":{"type":"trello","boardId":"1"}}""")]
    [InlineData("""{"name":"n","scope":{"bucketName":"b"}}""")]
    public async Task CreateSource_WithUnreadableScope_IsABadRequest(string json)
    {
        var response = await Admin.PostAsync("/api/sources", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a scope with no kind, or an unknown one, is the caller's mistake");
    }

    [Fact]
    public async Task UpdateSource_WithTypedScope_ReplacesItsKeysAndKeepsTheRest()
    {
        Guid connectionId = await SeedConnectionAsync(ConnectionProvider.S3, """{"region":"us-east-1"}""");
        var created = await Admin.PostAsJsonAsync("/api/sources", new
        {
            name = ShortName("upd"),
            connectionId,
            scopeJson = """{"bucketName":"b","prefix":"team/","note":"kept"}""",
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Guid sourceId = await SourceOf(created);

        var patched = await Admin.PatchAsJsonAsync($"/api/sources/{sourceId}", new { scope = new { type = "s3", bucketName = "b", prefix = "other/" } });

        patched.StatusCode.Should().Be(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
        StoredScopeShouldBe(sourceId, """{"bucketName":"b","prefix":"other/","note":"kept"}""");
    }

    public static TheoryData<string, string[]> Providers => new()
    {
        { "S3", ["s3"] },
        { "AzureBlob", ["azureBlob"] },
        { "Filesystem", ["filesystem"] },
        { "Sftp", ["sftp"] },
        { "GitHub", ["github"] },
        { "Atlassian", ["jira", "confluence"] },
        { "Custom", ["custom"] },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ScopeSchema_EveryProvider_DescribesEachKindItAccepts(string provider, string[] kinds)
    {
        var response = await Admin.GetAsync($"/api/v1/sources/scope-schema/{provider}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var listed = body.GetProperty("kinds").EnumerateArray().ToList();
        listed.Select(k => k.GetProperty("type").GetString()).Should().BeEquivalentTo(kinds);
        listed.Should().AllSatisfy(k => k.GetProperty("schema").GetProperty("properties").EnumerateObject().Should().NotBeEmpty());
    }

    [Fact]
    public async Task ScopeSchema_UnknownProvider_IsNotFound()
    {
        var response = await Admin.GetAsync("/api/v1/sources/scope-schema/trello");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string ShortName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> SeedConnectionAsync(ConnectionProvider provider, string configJson)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        return (await connections.CreateAsync(new CreateConnectionRequest(ShortName("conn"), provider, configJson), createdByUserId: null)).Id;
    }

    private static async Task<Guid> SourceOf(HttpResponseMessage created) =>
        (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    /// <summary>The source's stored scope is the given JSON, member order aside.</summary>
    private void StoredScopeShouldBe(Guid sourceId, string expected)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(sourceId).GetAwaiter().GetResult();
        JsonNode.DeepEquals(JsonNode.Parse(source!.ScopeJson), JsonNode.Parse(expected)).Should().BeTrue($"the stored scope is {source.ScopeJson}");
    }
}
