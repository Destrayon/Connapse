using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// GET /api/v1/audit/ (#880): administrators read what was done, newest first. That only administrators
/// may is the authorization matrix's.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AuditEndpointsTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ListAudit_ByAction_ReturnsTheEntriesNewestFirstWithTheirDetails()
    {
        string action = $"test.audit.{Guid.NewGuid():N}";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            await audit.LogAsync(action, "source", "first", new { Step = 1 });
            await audit.LogAsync(action, "source", "second", new { Step = 2 });
        }

        var page = await fixture.AdminClient.GetFromJsonAsync<JsonElement>($"/api/v1/audit/?action={action}");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("resourceId").GetString()).Should().Equal("second", "first");
        items[0].GetProperty("action").GetString().Should().Be(action);
        items[0].GetProperty("details").GetProperty("Step").GetInt32().Should().Be(2);
        page.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ListAudit_Paged_SaysWhetherMoreFollow()
    {
        string action = $"test.audit.{Guid.NewGuid():N}";
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
            for (int i = 0; i < 3; i++)
                await audit.LogAsync(action, "source", $"r{i}");
        }

        var first = await fixture.AdminClient.GetFromJsonAsync<JsonElement>($"/api/v1/audit/?action={action}&take=2");
        var rest = await fixture.AdminClient.GetFromJsonAsync<JsonElement>($"/api/v1/audit/?action={action}&take=2&skip=2");

        first.GetProperty("items").GetArrayLength().Should().Be(2);
        first.GetProperty("hasMore").GetBoolean().Should().BeTrue();
        rest.GetProperty("items").GetArrayLength().Should().Be(1);
        rest.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("knowledge:read", HttpStatusCode.Forbidden)]
    [InlineData("admin:audit", HttpStatusCode.OK)]
    public async Task ListAudit_AnOwnersToken_ReadsOnlyWithAdminAudit(string scope, HttpStatusCode expected)
    {
        // A token is limited to its scopes, whatever its owner's role allows.
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new Connapse.Core.PatCreateRequest($"audit-{Guid.NewGuid():N}", [scope]));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<Connapse.Core.PatCreateResponse>())!;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(Connapse.Identity.Authentication.ApiKeyAuthenticationOptions.HeaderName, pat.Token);

        using var response = await client.GetAsync("/api/v1/audit/?take=1");

        response.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("take=0", "invalid-page")]
    [InlineData("take=201", "invalid-page")]
    [InlineData("skip=-1", "invalid-page")]
    [InlineData("since=2026-10-02T00:00:00Z&until=2026-10-01T00:00:00Z", "invalid-audit-filter")]
    public async Task ListAudit_BadQuery_IsAValidationProblem(string query, string slug)
    {
        using var response = await fixture.AdminClient.GetAsync($"/api/v1/audit/?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().EndWith("#" + slug);
    }
}
