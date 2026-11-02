using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The Atlassian linking app over REST, through the real host and the Atlassian fake: administrators
/// only, nothing stored until Atlassian accepts the pair, the secret never returned, removal only
/// with the client ID typed, and the same audit events the Providers page writes.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class AtlassianLinkAppEndpointTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string LinkApp = "/api/v1/atlassian/linking-app";
    private const string Secret = "link-app-secret-value-789";
    private const string EditorEmail = "editor@atlassian-link-app-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";
    private const string InvalidGrant = """{"error":"invalid_grant"}""";

    private FakeAtlassianApi Api => fixture.Atlassian;

    public async Task InitializeAsync() => await RemoveStoredAppAsync();

    // The app is one row for the whole host, and the fake is shared: leave neither behind.
    public async Task DisposeAsync()
    {
        Api.FailTokenWith(null);
        await RemoveStoredAppAsync();
    }

    private async Task RemoveStoredAppAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>().DeleteAsync("atlassian");
    }

    private async Task<HttpClient> EditorClientAsync()
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            if (await users.FindByEmailAsync(EditorEmail) is null)
            {
                var user = new ConnapseUser
                {
                    UserName = EditorEmail,
                    Email = EditorEmail,
                    EmailConfirmed = true,
                    DisplayName = EditorEmail,
                    CreatedAt = DateTime.UtcNow,
                };
                (await users.CreateAsync(user, EditorPassword)).Succeeded.Should().BeTrue();
                await users.AddToRoleAsync(user, "Editor");
            }
        }

        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email = EditorEmail, password = EditorPassword });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<List<string>> AuditActionsAsync(DateTime since)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        return await db.AuditLogs
            .Where(a => a.ResourceType == "provider" && a.ResourceId == "atlassian" && a.CreatedAt >= since)
            .OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync();
    }

    private Task<HttpResponseMessage> SaveAsync(string clientId = "link-client", string secret = Secret) =>
        fixture.AdminClient.PutAsJsonAsync(LinkApp, new { clientId, clientSecret = secret });

    [Fact]
    public async Task EveryRoute_NonAdmin_Returns403()
    {
        using var editor = await EditorClientAsync();

        (await editor.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PutAsJsonAsync(LinkApp, new { clientId = "c", clientSecret = "s" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsync($"{LinkApp}/check", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PutAsJsonAsync($"{LinkApp}/sign-in", new { signInEnabled = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.DeleteAsync($"{LinkApp}?confirmClientId=c")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_NothingStored_Returns404()
    {
        (await fixture.AdminClient.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Save_Accepted_StoresTheAppAndReturnsItWithoutTheSecret()
    {
        DateTime since = DateTime.UtcNow;
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);

        var response = await SaveAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("clientId").GetString().Should().Be("link-client");
        body.GetProperty("signInEnabled").GetBoolean().Should().BeTrue();
        body.GetProperty("lastCheckAccepted").GetBoolean().Should().BeTrue();

        using var scope = fixture.Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>().GetAtlassianLinkAppSecretAsync())
            .Should().Be(Secret);
        (await AuditActionsAsync(since)).Should().ContainInOrder("provider.credential.checked", "provider.credential.saved");

        string got = await (await fixture.AdminClient.GetAsync(LinkApp)).Content.ReadAsStringAsync();
        got.Should().Contain("\"clientId\":\"link-client\"").And.NotContain(Secret);
    }

    [Fact]
    public async Task Save_Rejected_Returns422AndStoresNothing()
    {
        Api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");

        var response = await SaveAsync();

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("troubleshootingUrl").GetString()
            .Should().EndWith("#linking-app-rejected");
        (await fixture.AdminClient.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Save_AtlassianUnclear_Returns502AndStoresNothing()
    {
        Api.FailTokenWith(HttpStatusCode.InternalServerError);

        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await fixture.AdminClient.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Save_OverAStoredApp_WithoutReplaceExisting_Returns409AndChangesNothing()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        DateTime since = DateTime.UtcNow;

        var refused = await SaveAsync(clientId: "other-client", secret: "other-secret-value");

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("field").GetString().Should().Be("replaceExisting");
        body.GetProperty("error").GetString().Should().Contain("link-client").And.Contain("keeps their link")
            .And.Contain("replaceExisting: true");
        (await (await fixture.AdminClient.GetAsync(LinkApp)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("clientId").GetString().Should().Be("link-client");
        (await AuditActionsAsync(since)).Should().BeEmpty("Atlassian is not asked about an unconfirmed replace");

        var replaced = await fixture.AdminClient.PutAsJsonAsync(LinkApp,
            new { clientId = "other-client", clientSecret = "other-secret-value", replaceExisting = true });

        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString().Should().Be("other-client");
    }

    [Fact]
    public async Task Save_NothingStored_NeedsNoReplaceExisting()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);

        (await fixture.AdminClient.PutAsJsonAsync(LinkApp, new { clientId = "link-client", clientSecret = Secret, replaceExisting = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Save_MissingSecret_Returns400()
    {
        (await fixture.AdminClient.PutAsJsonAsync(LinkApp, new { clientId = "link-client" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Check_RecordsWhatAtlassianSaid()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        Api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");

        var response = await fixture.AdminClient.PostAsync($"{LinkApp}/check", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString().Should().Be("rejected");
        (await (await fixture.AdminClient.GetAsync(LinkApp)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lastCheckAccepted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Check_Rejected_ReturnsAtlassiansAnswerAsTheCardShowsIt_NeverTheSecret()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        Api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");
        DateTime since = DateTime.UtcNow;

        var response = await fixture.AdminClient.PostAsync($"{LinkApp}/check", null);

        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("detail").GetString().Should().Be("HTTP 401, access_denied");
        body.GetProperty("troubleshootingUrl").GetString().Should().EndWith("#linking-app-rejected");

        // One attempt: started and checked share its id, and the check says how long it took.
        using var scope = fixture.Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        var rows = await db.AuditLogs
            .Where(a => a.ResourceId == "atlassian" && a.CreatedAt >= since && a.Action.StartsWith("provider.credential.check"))
            .OrderBy(a => a.CreatedAt).ToListAsync();
        rows.Select(r => r.Action).Should().Equal("provider.credential.check_started", "provider.credential.checked");
        rows[1].Details!.RootElement.GetProperty("Attempt").GetString()
            .Should().Be(rows[0].Details!.RootElement.GetProperty("Attempt").GetString());
        rows[1].Details!.RootElement.GetProperty("TotalMs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        rows.Should().OnlyContain(r => !r.Details!.RootElement.GetRawText().Contains(Secret));
    }

    [Fact]
    public async Task Check_Accepted_HasNoDetail()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await (await fixture.AdminClient.PostAsync($"{LinkApp}/check", null)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("result").GetString().Should().Be("accepted");
        body.GetProperty("detail").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Save_RejectedOrUnclear_ReturnsAtlassiansAnswer()
    {
        Api.FailTokenWith(HttpStatusCode.Unauthorized, """{"error":"access_denied"}""");
        var rejected = await SaveAsync();
        (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()
            .Should().Be("HTTP 401, access_denied");

        Api.FailTokenWith(HttpStatusCode.InternalServerError);
        var unclear = await SaveAsync();
        unclear.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await unclear.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("detail").GetString().Should().StartWith("HTTP 500");
        body.GetProperty("troubleshootingUrl").GetString().Should().EndWith("#couldnt-reach-atlassian");
    }

    [Fact]
    public async Task Check_NothingStored_Returns404()
    {
        (await fixture.AdminClient.PostAsync($"{LinkApp}/check", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SignIn_TurnsOffAndOnWithoutTouchingTheApp_AndIsAudited()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        DateTime since = DateTime.UtcNow;

        var off = await fixture.AdminClient.PutAsJsonAsync($"{LinkApp}/sign-in", new { signInEnabled = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signInEnabled").GetBoolean().Should().BeFalse();
        var on = await fixture.AdminClient.PutAsJsonAsync($"{LinkApp}/sign-in", new { signInEnabled = true });
        (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("signInEnabled").GetBoolean().Should().BeTrue();

        (await fixture.AdminClient.PutAsJsonAsync($"{LinkApp}/sign-in", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AuditActionsAsync(since)).Should().Contain(["provider.sign_in.disabled", "provider.sign_in.enabled"]);
    }

    [Fact]
    public async Task Remove_NeedsTheClientIdTyped_ThenRemovesAndAudits()
    {
        Api.FailTokenWith(HttpStatusCode.BadRequest, InvalidGrant);
        (await SaveAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        DateTime since = DateTime.UtcNow;

        (await fixture.AdminClient.DeleteAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await fixture.AdminClient.DeleteAsync($"{LinkApp}?confirmClientId=other")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await fixture.AdminClient.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await fixture.AdminClient.DeleteAsync($"{LinkApp}?confirmClientId=link-client")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await fixture.AdminClient.GetAsync(LinkApp)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditActionsAsync(since)).Should().Contain("provider.credential.deleted");
    }
}
