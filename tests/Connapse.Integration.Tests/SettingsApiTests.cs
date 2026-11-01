using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>Every settings category over REST, resetting one (#922), and the settings actions (#950).</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SettingsApiTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task ApiKey_IsNeverReturnedAndKeptWhenLeftOut()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var original = await store.GetAsync<LlmSettings>("llm");
        try
        {
            var put = await fixture.AdminClient.PutAsJsonAsync("/api/settings/llm", new { model = "m-922", anthropicApiKey = "k-922" });
            put.StatusCode.Should().Be(HttpStatusCode.OK);

            var read = await fixture.AdminClient.GetFromJsonAsync<JsonElement>("/api/settings/llm");
            read.GetProperty("model").GetString().Should().Be("m-922");
            read.GetProperty("anthropicApiKey").ValueKind.Should().Be(JsonValueKind.Null);

            // What GET returned, sent back as it is, keeps the key it didn't show.
            (await fixture.AdminClient.PutAsync("/api/settings/llm", JsonContent.Create(read))).StatusCode.Should().Be(HttpStatusCode.OK);
            (await store.GetAsync<LlmSettings>("llm"))!.AnthropicApiKey.Should().Be("k-922");

            // An empty key clears it.
            (await fixture.AdminClient.PutAsJsonAsync("/api/settings/llm", new { model = "m-922", anthropicApiKey = "" })).EnsureSuccessStatusCode();
            (await store.GetAsync<LlmSettings>("llm"))!.AnthropicApiKey.Should().BeNull();
        }
        finally
        {
            if (original is null)
                await store.ResetAsync("llm");
            else
                await store.SaveAsync("llm", original);
        }
    }

    [Theory]
    [InlineData("samlsignin")]
    [InlineData("identitycenter")]
    [InlineData("azure")]
    [InlineData("azuread")]
    public async Task ProviderOwnedCategory_IsReadableButChangedOnlyThroughProviderSetup(string category)
    {
        (await fixture.AdminClient.GetAsync($"/api/settings/{category}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.AdminClient.PutAsJsonAsync($"/api/settings/{category}", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await fixture.AdminClient.DeleteAsync($"/api/v1/settings/{category}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("knowledge:read", HttpStatusCode.Forbidden)]
    [InlineData("admin:settings", HttpStatusCode.OK)]
    [InlineData("", HttpStatusCode.Forbidden)]
    public async Task GetSettings_AnOwnersToken_ReadsOnlyWithAdminSettings(string scope, HttpStatusCode expected)
    {
        // A token is limited to its scopes, whatever its owner's role allows; one issued with none has none.
        using var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/auth/pats",
            new PatCreateRequest($"settings-{Guid.NewGuid():N}", scope.Length == 0 ? [] : [scope]));
        created.EnsureSuccessStatusCode();
        var pat = (await created.Content.ReadFromJsonAsync<PatCreateResponse>())!;
        using var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(Connapse.Identity.Authentication.ApiKeyAuthenticationOptions.HeaderName, pat.Token);

        using var response = await client.GetAsync("/api/settings/chunking");

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Reset_UnknownCategory_Returns404()
    {
        var response = await fixture.AdminClient.DeleteAsync("/api/v1/settings/colors");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().Should().EndWith("#settings-category-not-found");
    }

    [Fact]
    public async Task Update_ValueTheCategoryRefuses_Returns400WithTheField()
    {
        var response = await fixture.AdminClient.PutAsJsonAsync("/api/settings/search", new { fusionAlpha = 2f });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("fusionAlpha");
    }

    [Fact]
    public async Task Reindex_ContainerIdNotAnId_Returns400WithTheField()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/reindex", new { containerId = "nope" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("containerId");
    }

    [Fact]
    public async Task TestConnection_CategoryWithoutATest_IsAFailedTest()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/settings/test-connection", new { category = "chunking", settings = new { } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task EmbeddingModels_NamesTheCurrentModel()
    {
        var models = await fixture.AdminClient.GetFromJsonAsync<JsonElement>("/api/settings/embedding-models");

        models.GetProperty("currentModel").GetString().Should().NotBeNullOrEmpty();
        models.GetProperty("models").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
