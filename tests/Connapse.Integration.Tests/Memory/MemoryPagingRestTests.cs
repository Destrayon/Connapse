using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-241 through the real host: the four paged memory reads over REST page a space to the end with the cursors they
/// return, count it, list its subjects and a fact's history, and refuse unreadable query values by field.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemoryPagingRestTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task PagedReads_OverRest_PageCountAndRefuseByField()
    {
        HttpClient http = fixture.AdminClient;
        string name = $"paging-rest-{Guid.NewGuid():N}"[..40];
        HttpResponseMessage create = await http.PostAsJsonAsync("/api/containers", new { name, kind = "memory" });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        string space = (await Json(create)).GetProperty("id").GetString()!;
        try
        {
            Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
            List<Guid> facts = [];
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                IMemoryService memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
                foreach (string subject in new[] { "alpha", "beta", "gamma" })
                {
                    MemoryWriteResult saved = await memory.RememberAsync(Guid.Parse(space),
                        new NewMemoryFact(subject, "colour", $"{subject}-blue", null, null, [], null, MemoryScope.Team),
                        new MemoryCaller(admin, null, "admin"));
                    facts.Add(saved.Fact!.Id);
                }
            }

            // Every fact exactly once, two to a page, by id and by name.
            List<string> seen = [];
            string? cursor = null;
            int pages = 0;
            do
            {
                string query = cursor is null ? "limit=2" : $"limit=2&cursor={Uri.EscapeDataString(cursor)}";
                JsonElement page = await OkJson(await http.GetAsync($"/api/memory/spaces/{name}/facts?{query}"));
                seen.AddRange(page.GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("fact").GetProperty("id").GetString()!));
                cursor = page.GetProperty("nextCursor").GetString();
                pages++;
            }
            while (cursor is not null);
            pages.Should().Be(2);
            seen.Should().BeEquivalentTo(facts.Select(f => f.ToString()));

            JsonElement count = await OkJson(await http.GetAsync($"/api/memory/spaces/{space}/facts/count?q=blue"));
            count.GetProperty("count").GetInt64().Should().Be(3);
            count.GetProperty("capped").GetBoolean().Should().BeFalse();

            JsonElement subjects = await OkJson(await http.GetAsync($"/api/memory/spaces/{space}/subjects?limit=100"));
            subjects.GetProperty("subjects").EnumerateArray().Select(s => s.GetProperty("subject").GetString())
                .Should().Equal("alpha", "beta", "gamma");

            JsonElement history = await OkJson(await http.GetAsync($"/api/memory/facts/{facts[0]}/history"));
            history.GetProperty("total").GetInt64().Should().Be(1);
            history.GetProperty("versions").GetArrayLength().Should().Be(1);

            // Values that can't be read are refused with the field they came in.
            foreach ((string query, string field) in new[]
            {
                ("sort=loudest", "sort"), ("cites=7", "cites"), ("from=yesterday", "from"), ("status=old", "status"),
                ("author=bob", "author"), ("limit=101", "limit"), ("cursor=garbage", "cursor"), ("sort=relevance", "sort"),
            })
            {
                HttpResponseMessage refused = await http.GetAsync($"/api/memory/spaces/{space}/facts?{query}");
                refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, query);
                (await Json(refused)).GetProperty("field").GetString().Should().Be(field, query);
            }

            (await http.GetAsync($"/api/memory/facts/{Guid.NewGuid()}/history")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await http.GetAsync($"/api/memory/spaces/{Guid.NewGuid()}/facts")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(Guid.Parse(space));
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> OkJson(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement;
    }
}
