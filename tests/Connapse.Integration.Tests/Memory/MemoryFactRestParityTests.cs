using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.Authorization;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-282 through the real host: a script remembers, recalls, corrects and forgets facts over REST and gets what the
/// memory tools give an agent acting as the same user, and a caller who can't see a space is refused there on both.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryFactRestParityTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task RememberRecallCorrectForget_OverRest_MatchTheMemoryTools()
    {
        HttpClient rest = fixture.AdminClient;
        string space = await CreateSpaceAsync(rest, $"fact-parity-{Guid.NewGuid():N}"[..40]);
        string subject = $"halcyon-{Guid.NewGuid():N}";
        try
        {
            await using McpClient mcp = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read", "knowledge:write"));

            // One fact saved each way lands in the same space, and each surface recalls both.
            HttpResponseMessage created = await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "database", value = "PostgreSQL 15", validFrom = "2026-03-01" });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            JsonElement remembered = await Json(created);
            remembered.GetProperty("savedTo").GetProperty("id").GetString().Should().Be(space);
            string database = remembered.GetProperty("fact").GetProperty("id").GetString()!;
            created.Headers.Location!.ToString().Should().Be($"/api/memory/facts/{database}/history");

            string cache = Id(await CallAsync(mcp, "memory_remember", new()
            {
                ["space"] = space,
                ["subject"] = subject,
                ["attribute"] = "cache",
                ["value"] = "Redis 7",
                ["validFrom"] = "2026-03-01",
            }), "remembered");

            await RecallsSameAsync(rest, mcp, space, subject, "PostgreSQL 15", "Redis 7");
            (await RestRecallAsync(rest, $"subject={subject}&asOf=2026-02-01")).Should().BeEmpty();
            (await McpRecallAsync(mcp, new() { ["subject"] = subject, ["asOf"] = "2026-02-01" })).Should().BeEmpty();

            // A change corrected over REST and a mistake corrected by the tool read back the same way on both.
            HttpResponseMessage changed = await rest.PostAsJsonAsync($"/api/memory/facts/{database}/correct",
                new { value = "PostgreSQL 16", kind = "changed", validFrom = "2026-06-01" });
            changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
            JsonElement correction = await Json(changed);
            correction.GetProperty("replaced").GetProperty("id").GetString().Should().Be(database);
            string newDatabase = correction.GetProperty("fact").GetProperty("id").GetString()!;

            string wrong = await CallAsync(mcp, "memory_correct", new() { ["factId"] = cache, ["value"] = "Redis 7.2", ["kind"] = "wrong" });
            JsonDocument.Parse(wrong).RootElement.GetProperty("replaced").GetProperty("id").GetString().Should().Be(cache);
            string newCache = Id(wrong, "remembered");

            await RecallsSameAsync(rest, mcp, space, subject, "PostgreSQL 16", "Redis 7.2");
            (await RestRecallAsync(rest, $"space={space}&subject={subject}&includeHistory=true")).Select(f => f.Id)
                .Should().BeEquivalentTo((await McpRecallAsync(mcp, new() { ["space"] = space, ["subject"] = subject, ["includeHistory"] = true })).Select(f => f.Id))
                .And.HaveCount(4);

            // Forgetting deletes every version on both.
            HttpResponseMessage forgot = await rest.DeleteAsync($"/api/memory/facts/{newDatabase}");
            forgot.StatusCode.Should().Be(HttpStatusCode.OK, await forgot.Content.ReadAsStringAsync());
            (await Json(forgot)).GetProperty("deleted").GetInt32().Should().Be(2);
            (await CallAsync(mcp, "memory_forget", new() { ["factId"] = newCache })).Should().Be($"Forgot fact '{newCache}' (2 version(s) deleted).");
            (await RestRecallAsync(rest, $"space={space}&subject={subject}&includeHistory=true")).Should().BeEmpty();
            (await McpRecallAsync(mcp, new() { ["space"] = space, ["subject"] = subject, ["includeHistory"] = true })).Should().BeEmpty();

            // Values that can't be read are refused with the field they came in.
            await RefusedAsync(await rest.PostAsJsonAsync($"/api/memory/facts/{newDatabase}/correct", new { value = "v", kind = "maybe" }), "kind");
            await RefusedAsync(await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "a", value = "v", validFrom = "soon" }), "validFrom");
            await RefusedAsync(await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "a", value = "v", sources = new[] { "HAL-17" } }), "sources");
            await RefusedAsync(await rest.GetAsync($"/api/memory/facts?subject={subject}&asOf=yesterday"), "asOf");
            await RefusedAsync(await rest.GetAsync($"/api/memory/facts?subject={subject}&cursor=abc&asOf=2026-01-01"), "cursor");
        }
        finally
        {
            await DeleteSpacesAsync(space);
        }
    }

    [Fact]
    public async Task PersonalSpace_IsNotFoundOverRestForAnotherUser_AndAViewerCannotRememberOverRest()
    {
        HttpClient admin = fixture.AdminClient;
        string team = await CreateSpaceAsync(admin, $"fact-refuse-{Guid.NewGuid():N}"[..40]);
        string? personal = null;
        string subject = $"lunch-{Guid.NewGuid():N}";
        using HttpClient viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
        try
        {
            // A Viewer's agent keeps facts in the Viewer's personal space, and the Viewer reads them over REST.
            string fact;
            await using (McpClient owner = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Viewer), "knowledge:read", "knowledge:write")))
            {
                // No space is made for an agent (CON-291), so it makes its own personal one first.
                await CallAsync(owner, "memory_space_create", new() { ["name"] = "my-memory", ["kind"] = "personal" });
                string saved = await CallAsync(owner, "memory_remember", new()
                {
                    ["space"] = "my-memory",
                    ["subject"] = subject,
                    ["attribute"] = "lunch",
                    ["value"] = "Tacos on Tuesdays",
                });
                fact = Id(saved, "remembered");
                personal = JsonDocument.Parse(saved).RootElement.GetProperty("savedTo").GetProperty("id").GetString()!;
            }
            (await RestRecallAsync(viewer, $"space={personal}&subject={subject}")).Select(f => f.Id).Should().Equal(fact);

            // REST writes are for Editors, as the other memory writes are: a Viewer saves nowhere over REST.
            (await viewer.PostAsJsonAsync($"/api/memory/spaces/{team}/facts", new { subject, attribute = "a", value = "v" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await viewer.PostAsJsonAsync("/api/memory/spaces/my-memory/facts", new { subject, attribute = "a", value = "v" }))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Anyone else, an administrator included, finds nothing there, over REST as over MCP.
            (await admin.GetAsync($"/api/memory/facts?space={personal}&subject={subject}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await admin.PostAsJsonAsync($"/api/memory/spaces/{personal}/facts", new { subject, attribute = "a", value = "v" }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await admin.PostAsJsonAsync($"/api/memory/facts/{fact}/correct", new { value = "Pizza", kind = "wrong" }))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await admin.DeleteAsync($"/api/memory/facts/{fact}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await RestRecallAsync(admin, $"subject={subject}")).Should().BeEmpty();

            await using (McpClient stranger = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read", "knowledge:write")))
            {
                (await CallAsync(stranger, "memory_recall", new() { ["space"] = personal, ["subject"] = subject }))
                    .Should().ContainEquivalentOf("not found");
                (await CallAsync(stranger, "memory_forget", new() { ["factId"] = fact })).Should().ContainEquivalentOf("not found");
            }

            (await RestRecallAsync(viewer, $"space={personal}&subject={subject}")).Select(f => f.Id).Should().Equal(fact);
        }
        finally
        {
            await DeleteSpacesAsync(team, personal);
        }
    }

    [Fact]
    public async Task Timeline_OverRest_MatchesTheMemoryTimelineTool_AndRefusesWhatItRefuses()
    {
        HttpClient rest = fixture.AdminClient;
        string space = await CreateSpaceAsync(rest, $"fact-timeline-{Guid.NewGuid():N}"[..40]);
        string? personal = null;
        string subject = $"orion-{Guid.NewGuid():N}";
        using HttpClient viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
        try
        {
            await using McpClient mcp = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read", "knowledge:write"));

            // A value that changed twice, plus one that started in March and never changed.
            string database = Id(await CallAsync(mcp, "memory_remember", new()
            {
                ["space"] = space,
                ["subject"] = subject,
                ["attribute"] = "database",
                ["value"] = "PostgreSQL 14",
                ["validFrom"] = "2026-01-10",
            }), "remembered");
            string second = Id(await CallAsync(mcp, "memory_correct", new()
            {
                ["factId"] = database,
                ["value"] = "PostgreSQL 15",
                ["kind"] = "changed",
                ["validFrom"] = "2026-04-01",
            }), "remembered");
            await CallAsync(mcp, "memory_correct", new()
            {
                ["factId"] = second,
                ["value"] = "PostgreSQL 16",
                ["kind"] = "changed",
                ["validFrom"] = "2026-08-01",
            });
            await CallAsync(mcp, "memory_remember", new()
            {
                ["space"] = space,
                ["subject"] = subject,
                ["attribute"] = "cache",
                ["value"] = "Redis 7",
                ["validFrom"] = "2026-03-01",
            });

            // Every shape of read: one space paged, a window in one space, a window everywhere, one attribute everywhere.
            (string Query, Dictionary<string, object?> Arguments)[] reads =
            [
                ($"space={space}&subject={subject}", new() { ["space"] = space, ["subject"] = subject }),
                ($"space={space}&subject={subject}&from=2026-02-01&to=2026-05-01",
                    new() { ["space"] = space, ["subject"] = subject, ["from"] = "2026-02-01", ["to"] = "2026-05-01" }),
                ($"subject={subject}&from=2026-07-01", new() { ["subject"] = subject, ["from"] = "2026-07-01" }),
                ($"subject={subject}&attribute=database", new() { ["subject"] = subject, ["attribute"] = "database" }),
            ];
            foreach ((string query, Dictionary<string, object?> arguments) in reads)
            {
                List<Recalled> overRest = await RestTimelineAsync(rest, query);
                overRest.Should().NotBeEmpty(query);
                overRest.Should().Equal(await McpTimelineAsync(mcp, arguments), query);
            }

            // In date order, ended and corrected versions included.
            (await RestTimelineAsync(rest, $"space={space}&subject={subject}")).Select(f => f.Value)
                .Should().Equal("PostgreSQL 14", "Redis 7", "PostgreSQL 15", "PostgreSQL 16");

            // One space paged a fact at a time reaches every fact, the same pages on both.
            List<Recalled> paged = [];
            string? cursor = null;
            do
            {
                HttpResponseMessage page = await rest.GetAsync(
                    $"/api/memory/timeline?space={space}&subject={subject}&limit=1{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}");
                page.StatusCode.Should().Be(HttpStatusCode.OK, await page.Content.ReadAsStringAsync());
                JsonElement body = await Json(page);
                paged.AddRange(Facts(body.GetProperty("facts"), f => f.GetProperty("fact")));
                cursor = body.GetProperty("nextCursor").GetString();
            }
            while (cursor is not null && paged.Count < 10);
            paged.Select(f => f.Value).Should().BeEquivalentTo("PostgreSQL 14", "Redis 7", "PostgreSQL 15", "PostgreSQL 16");

            // Values that can't be read are refused with the field they came in.
            await RefusedAsync(await rest.GetAsync($"/api/memory/timeline?subject={subject}&from=lately"), "from");
            await RefusedAsync(await rest.GetAsync($"/api/memory/timeline?subject={subject}&to=soon"), "to");
            await RefusedAsync(await rest.GetAsync($"/api/memory/timeline?space={space}&from=2026-01-01&cursor=abc"), "cursor");

            // Someone else's personal space is not found over REST, as over MCP, and its facts never show in a read of everything.
            await using (McpClient owner = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Viewer), "knowledge:read", "knowledge:write")))
            {
                // No space is made for an agent (CON-291), so it makes its own personal one first.
                await CallAsync(owner, "memory_space_create", new() { ["name"] = "my-memory", ["kind"] = "personal" });
                personal = JsonDocument.Parse(await CallAsync(owner, "memory_remember", new()
                {
                    ["space"] = "my-memory",
                    ["subject"] = subject,
                    ["attribute"] = "lunch",
                    ["value"] = "Tacos on Tuesdays",
                })).RootElement.GetProperty("savedTo").GetProperty("id").GetString()!;
            }
            (await RestTimelineAsync(viewer, $"space={personal}&subject={subject}")).Select(f => f.Value).Should().Equal("Tacos on Tuesdays");
            (await rest.GetAsync($"/api/memory/timeline?space={personal}&subject={subject}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await CallAsync(mcp, "memory_timeline", new() { ["space"] = personal, ["subject"] = subject })).Should().ContainEquivalentOf("not found");
            (await RestTimelineAsync(rest, $"subject={subject}&attribute=lunch")).Should().BeEmpty();
        }
        finally
        {
            await DeleteSpacesAsync(space, personal);
        }
    }

    /// <summary>
    /// CON-340: a fact past its check-by date still comes back from recall, marked unconfirmed, on both surfaces; the
    /// due filter returns only those; confirming one or saving it again word for word moves its date on; and a space's
    /// default period applies to a fact saved without one.
    /// </summary>
    [Fact]
    public async Task CheckBy_PastDueFact_IsRecalledUnconfirmed_FilteredAsDue_AndConfirmingClearsIt()
    {
        HttpClient rest = fixture.AdminClient;
        string space = await CreateSpaceAsync(rest, $"fact-check-{Guid.NewGuid():N}"[..40]);
        string subject = $"con-{Guid.NewGuid():N}";
        try
        {
            await using McpClient mcp = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read", "knowledge:write"));

            HttpResponseMessage set = await rest.PutAsJsonAsync($"/api/memory/spaces/{space}/check-defaults",
                new { days = new Dictionary<string, int> { ["status"] = 7 } });
            set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
            JsonElement defaults = await Json(await rest.GetAsync($"/api/memory/spaces/{space}/check-defaults"));
            defaults.GetProperty("days").GetProperty("status").GetInt32().Should().Be(7);

            HttpResponseMessage stale = await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "waiting_on", value = "a review", checkBy = "2020-01-01" });
            stale.StatusCode.Should().Be(HttpStatusCode.Created, await stale.Content.ReadAsStringAsync());
            string waiting = (await Json(stale)).GetProperty("fact").GetProperty("id").GetString()!;

            string owner = Id(await CallAsync(mcp, "memory_remember", new()
            {
                ["space"] = space, ["subject"] = subject, ["attribute"] = "owner", ["value"] = "Ana", ["checkBy"] = "never",
            }), "remembered");
            HttpResponseMessage status = await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "status", value = "in review" });
            JsonElement statusFact = (await Json(status)).GetProperty("fact");
            DateTimeOffset.Parse(statusFact.GetProperty("checkBy").GetString()!)
                .Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromHours(1), "the space's default for status applies");

            // Past its date the fact is still recalled, marked, with when it was saved and that it was never confirmed.
            string all = await CallAsync(mcp, "memory_recall", new() { ["space"] = space, ["subject"] = subject });
            JsonElement shown = JsonDocument.Parse(all).RootElement.GetProperty("facts").EnumerateArray()
                .Single(f => f.GetProperty("id").GetString() == waiting);
            shown.GetProperty("unconfirmed").GetBoolean().Should().BeTrue();
            shown.GetProperty("check").GetString().Should().Contain("last confirmed never");
            (await McpRecallAsync(mcp, new() { ["space"] = space, ["subject"] = subject })).Should().HaveCount(3);

            (await McpRecallAsync(mcp, new() { ["space"] = space, ["subject"] = subject, ["due"] = true }))
                .Select(f => f.Id).Should().Equal(waiting);
            (await RestRecallAsync(rest, $"space={space}&subject={subject}&check=due")).Select(f => f.Id).Should().Equal(waiting);
            (await RestRecallAsync(rest, $"space={space}&subject={subject}&check=fresh")).Select(f => f.Id)
                .Should().BeEquivalentTo([owner, statusFact.GetProperty("id").GetString()]);
            await RefusedAsync(await rest.GetAsync($"/api/memory/facts?space={space}&check=stale"), "check");
            await RefusedAsync(await rest.PostAsJsonAsync($"/api/memory/spaces/{space}/facts",
                new { subject, attribute = "x", value = "y", checkBy = "0d" }), "checkBy");

            // Confirming keeps the fact and moves its date on, so nothing is due any more.
            HttpResponseMessage confirmed = await rest.PostAsJsonAsync($"/api/memory/facts/{waiting}/confirm", new { checkBy = "30d" });
            confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
            JsonElement fact = (await Json(confirmed)).GetProperty("fact");
            fact.GetProperty("id").GetString().Should().Be(waiting);
            fact.GetProperty("unconfirmed").GetBoolean().Should().BeFalse();
            fact.GetProperty("confirmedAt").ValueKind.Should().Be(JsonValueKind.String);
            (await McpRecallAsync(mcp, new() { ["space"] = space, ["subject"] = subject, ["due"] = true })).Should().BeEmpty();

            // The same fact saved again word for word confirms it rather than saving it twice.
            string again = await CallAsync(mcp, "memory_remember", new()
            {
                ["space"] = space, ["subject"] = subject, ["attribute"] = "owner", ["value"] = "Ana",
            });
            JsonDocument.Parse(again).RootElement.GetProperty("confirmed").GetBoolean().Should().BeTrue(again);
            (await CallAsync(mcp, "memory_confirm", new() { ["factId"] = Guid.NewGuid().ToString() })).Should().ContainEquivalentOf("not found");
        }
        finally
        {
            await DeleteSpacesAsync(space);
        }
    }

    private static async Task<List<Recalled>> RestTimelineAsync(HttpClient http, string query)
    {
        HttpResponseMessage response = await http.GetAsync($"/api/memory/timeline?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return Facts((await Json(response)).GetProperty("facts"), f => f.GetProperty("fact"));
    }

    private static async Task<List<Recalled>> McpTimelineAsync(McpClient mcp, Dictionary<string, object?> arguments)
    {
        string read = await CallAsync(mcp, "memory_timeline", arguments);
        read.Should().StartWith("{", read);
        return Facts(JsonDocument.Parse(read).RootElement.GetProperty("facts"), f => f);
    }

    private static List<Recalled> Facts(JsonElement facts, Func<JsonElement, JsonElement> fact) =>
        [.. facts.EnumerateArray().Select(fact).Select(f => new Recalled(f.GetProperty("id").GetString()!, f.GetProperty("value").GetString()!))];

    private sealed record Recalled(string Id, string Value);

    private static async Task RecallsSameAsync(HttpClient rest, McpClient mcp, string space, string subject, params string[] values)
    {
        foreach (string? named in new[] { space, null })
        {
            List<Recalled> overRest = await RestRecallAsync(rest, named is null ? $"subject={subject}" : $"space={named}&subject={subject}");
            List<Recalled> overMcp = await McpRecallAsync(mcp, named is null
                ? new() { ["subject"] = subject }
                : new() { ["space"] = named, ["subject"] = subject });
            overRest.Select(f => f.Value).Should().BeEquivalentTo(values);
            overRest.Should().BeEquivalentTo(overMcp);
        }
    }

    private static async Task<List<Recalled>> RestRecallAsync(HttpClient http, string query)
    {
        HttpResponseMessage response = await http.GetAsync($"/api/memory/facts?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await Json(response)).GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("fact"))
            .Select(f => new Recalled(f.GetProperty("id").GetString()!, f.GetProperty("value").GetString()!))];
    }

    private static async Task<List<Recalled>> McpRecallAsync(McpClient mcp, Dictionary<string, object?> arguments)
    {
        string recalled = await CallAsync(mcp, "memory_recall", arguments);
        recalled.Should().StartWith("{", recalled);
        return [.. JsonDocument.Parse(recalled).RootElement.GetProperty("facts").EnumerateArray()
            .Select(f => new Recalled(f.GetProperty("id").GetString()!, f.GetProperty("value").GetString()!))];
    }

    private static async Task RefusedAsync(HttpResponseMessage response, string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("field").GetString().Should().Be(field);
    }

    private static string Id(string toolAnswer, string property)
    {
        toolAnswer.Should().StartWith("{", toolAnswer);
        return JsonDocument.Parse(toolAnswer).RootElement.GetProperty(property).GetProperty("id").GetString()!;
    }

    private static async Task<string> CreateSpaceAsync(HttpClient http, string name)
    {
        HttpResponseMessage create = await http.PostAsJsonAsync("/api/containers", new { name, kind = "memory" });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await Json(create)).GetProperty("id").GetString()!;
    }

    private async Task DeleteSpacesAsync(params string?[] spaces)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        foreach (string? space in spaces)
            if (space is not null)
                await containers.DeleteAsync(Guid.Parse(space));
    }

    /// <summary>A key for a new agent <paramref name="user"/> created, so it acts as that user.</summary>
    private async Task<Action<HttpRequestHeaders>> AgentKeyAsync(Guid user, params string[] scopes)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var agent = await agents.CreateAsync(new CreateAgentRequest($"fact-{Guid.NewGuid():N}"), user);
        var key = await agents.CreateKeyAsync(agent!.Id, new CreateAgentKeyRequest("fact", scopes, RequiresApproval: false));
        return headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, key!.Token);
    }

    private async Task<McpClient> ConnectAsync(Action<HttpRequestHeaders> credential)
    {
        var http = fixture.Factory.CreateClient();
        credential(http.DefaultRequestHeaders);
        return await McpTestClient.ConnectAsync(http);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
