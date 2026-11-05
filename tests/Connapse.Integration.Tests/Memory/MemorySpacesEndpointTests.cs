using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Integration.Tests.Authorization;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// <c>/api/memory/spaces</c> end to end (CON-240): make, list, star, rename and delete spaces over REST, with a
/// documents container and someone else's personal space refused the way the operations say.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpacesEndpointTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Owner => fixture.AdminClient;

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode status)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    private async Task<MemorySpaceView> CreateAsync(HttpClient client, string name, string kind) =>
        await ReadAsync<MemorySpaceView>(
            await client.PostAsJsonAsync("/api/memory/spaces", new { name, kind }), HttpStatusCode.Created);

    private async Task<MemorySpacePage> ListAsync(HttpClient client, string query) =>
        await ReadAsync<MemorySpacePage>(await client.GetAsync($"/api/memory/spaces?{query}"), HttpStatusCode.OK);

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("type", out JsonElement type) ? type.GetString() : null;

    [Fact]
    public async Task CreateListStar_PagesStarredSpacesFirstWithCounts()
    {
        string tag = $"ms-{Guid.NewGuid():N}"[..20];
        MemorySpaceView team = await CreateAsync(Owner, $"{tag}-team", "team");
        MemorySpaceView personal = await CreateAsync(Owner, $"{tag}-own", "personal");
        MemorySpaceView other = await CreateAsync(Owner, $"{tag}-zzz", "team");

        (await Owner.PutAsync($"/api/memory/spaces/{other.Id}/star", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        MemorySpacePage first = await ListAsync(Owner, $"q={tag}&sort=name&limit=2");
        MemorySpacePage second = await ListAsync(Owner, $"q={tag}&sort=name&limit=2&cursor={Uri.EscapeDataString(first.NextCursor!)}");

        team.Kind.Should().Be(MemorySpaceKind.Team);
        personal.Kind.Should().Be(MemorySpaceKind.Personal);
        first.Items.Select(i => i.Name).Concat(second.Items.Select(i => i.Name))
            .Should().Equal($"{tag}-zzz", $"{tag}-own", $"{tag}-team");
        first.Items[0].Starred.Should().BeTrue();
        first.Total.Should().Be(3);
        second.NextCursor.Should().BeNull();
        first.Counts.Should().Be(new MemorySpaceCounts(All: 3, Starred: 1, Personal: 1, Team: 2, Containers: 0));
        (await ListAsync(Owner, $"q={tag}&kind=personal")).Items.Select(i => i.Id).Should().Equal(personal.Id);

        (await Owner.DeleteAsync($"/api/memory/spaces/{other.Id}/star")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(Owner, $"q={tag}&starred=true")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_BadKindOrCursor_IsABadRequestNamingTheField()
    {
        HttpResponseMessage kind = await Owner.GetAsync("/api/memory/spaces?kind=bogus");
        HttpResponseMessage cursor = await Owner.GetAsync("/api/memory/spaces?cursor=not-a-cursor");

        kind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(kind)).Should().EndWith("invalid-memory-space");
        cursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(cursor)).Should().EndWith("invalid-page");
    }

    [Fact]
    public async Task Update_RenamesAndRefusesATakenName()
    {
        string tag = $"ms-{Guid.NewGuid():N}"[..20];
        MemorySpaceView space = await CreateAsync(Owner, $"{tag}-a", "team");
        await CreateAsync(Owner, $"{tag}-b", "team");

        MemorySpaceView renamed = await ReadAsync<MemorySpaceView>(
            await Owner.PatchAsJsonAsync($"/api/memory/spaces/{space.Id}", new { name = $"{tag}-c", description = "notes" }), HttpStatusCode.OK);
        HttpResponseMessage taken = await Owner.PatchAsJsonAsync($"/api/memory/spaces/{space.Id}", new { name = $"{tag}-b" });

        renamed.Name.Should().Be($"{tag}-c");
        renamed.Description.Should().Be("notes");
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(taken)).Should().EndWith("memory-space-conflict");
    }

    [Fact]
    public async Task Delete_DryRunReportsThenDeleteRemoves()
    {
        string tag = $"ms-{Guid.NewGuid():N}"[..20];
        MemorySpaceView space = await CreateAsync(Owner, $"{tag}-gone", "team");

        MemorySpaceDeletion dry = await ReadAsync<MemorySpaceDeletion>(
            await Owner.DeleteAsync($"/api/memory/spaces/{space.Id}?dryRun=true"), HttpStatusCode.OK);
        (await ListAsync(Owner, $"q={tag}")).Total.Should().Be(1, "a dry run deletes nothing");
        MemorySpaceDeletion done = await ReadAsync<MemorySpaceDeletion>(
            await Owner.DeleteAsync($"/api/memory/spaces/{space.Id}"), HttpStatusCode.OK);

        dry.Should().Be(new MemorySpaceDeletion(space.Id, space.Name, MemorySpaceKind.Team, 0, 0, 0, false));
        done.Deleted.Should().BeTrue();
        (await ListAsync(Owner, $"q={tag}")).Total.Should().Be(0);
        (await Owner.DeleteAsync($"/api/memory/spaces/{space.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DocumentsContainer_IsManagedThroughContainerRoutes()
    {
        string name = $"ms-{Guid.NewGuid():N}"[..20];
        Container container = await ReadAsync<Container>(
            await Owner.PostAsJsonAsync("/api/containers", new { name }), HttpStatusCode.Created);

        HttpResponseMessage rename = await Owner.PatchAsJsonAsync($"/api/memory/spaces/{container.Id}", new { name = $"{name}-x" });
        HttpResponseMessage delete = await Owner.DeleteAsync($"/api/memory/spaces/{container.Id}");
        HttpResponseMessage make = await Owner.PostAsJsonAsync("/api/memory/spaces", new { name = $"{name}-y", kind = "container" });

        rename.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(rename)).Should().EndWith("memory-space-is-container");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        make.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Owner.PutAsync($"/api/memory/spaces/{container.Id}/star", null)).StatusCode.Should().Be(HttpStatusCode.NoContent,
            "any space the caller can see may be starred");
        await Owner.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task PersonalSpace_IsInvisibleToEveryoneElse()
    {
        string tag = $"ms-{Guid.NewGuid():N}"[..20];
        MemorySpaceView mine = await CreateAsync(Owner, $"{tag}-own", "personal");
        HttpClient editor = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Editor);
        HttpClient viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);

        (await ListAsync(editor, $"q={tag}")).Items.Should().BeEmpty();
        (await editor.PatchAsJsonAsync($"/api/memory/spaces/{mine.Id}", new { name = $"{tag}-x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await editor.DeleteAsync($"/api/memory/spaces/{mine.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viewer.PutAsync($"/api/memory/spaces/{mine.Id}/star", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await viewer.PostAsJsonAsync("/api/memory/spaces", new { name = $"{tag}-v", kind = "team" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a viewer can't make spaces");
    }
}
