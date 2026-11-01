using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Links;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for GET/PUT /api/containers/{id}/settings — per-container settings overrides.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerSettingsIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetContainerSettings_NewContainer_ReturnsEmptyOverrides()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-empty-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var response = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container!.Id}/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var overrides = await response.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);
        overrides.Should().NotBeNull();
        overrides!.Chunking.Should().BeNull();
        overrides.Embedding.Should().BeNull();
        overrides.Search.Should().BeNull();
        overrides.Upload.Should().BeNull();

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_EmbeddingOverride_PersistsAndRetrieves()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-embed-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        var overrides = new ContainerSettingsOverrides
        {
            Embedding = new EmbeddingSettings
            {
                Provider = "OpenAI",
                Model = "text-embedding-3-small",
                Dimensions = 1536
            }
        };

        var putResponse = await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container!.Id}/settings", overrides);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/settings");
        var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

        retrieved.Should().NotBeNull();
        retrieved!.Embedding.Should().NotBeNull();
        retrieved.Embedding!.Provider.Should().Be("OpenAI");
        retrieved.Embedding.Model.Should().Be("text-embedding-3-small");
        retrieved.Embedding.Dimensions.Should().Be(1536);
        retrieved.Chunking.Should().BeNull("only embedding was overridden");

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_ResetToEmpty_ClearsOverrides()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-reset-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        // Set an override
        var overrides = new ContainerSettingsOverrides
        {
            Search = new SearchSettings { Mode = "Keyword", TopK = 5 }
        };
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container!.Id}/settings", overrides);

        // Clear it
        var empty = new ContainerSettingsOverrides();
        await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{container.Id}/settings", empty);

        var getResponse = await fixture.AdminClient.GetAsync(
            $"/api/containers/{container.Id}/settings");
        var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

        retrieved!.Search.Should().BeNull("overrides were cleared");

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_WithKeys_ReadsBackWithoutThemAndKeepsThemWhenLeftOut()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-keys-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        var id = Guid.Parse(container!.Id);

        var put = await fixture.AdminClient.PutAsJsonAsync($"/api/containers/{id}/settings", new ContainerSettingsOverrides
        {
            Embedding = new EmbeddingSettings { Provider = "OpenAI", Model = "text-embedding-3-small", Dimensions = 1536, OpenAiApiKey = "sk-con186-embedding" },
            Search = new SearchSettings { CrossEncoderProvider = "Cohere", CrossEncoderApiKey = "co-con186-rerank" },
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        (await put.Content.ReadAsStringAsync()).Should().NotContain("con186");

        // Every read leaves the keys out: the settings, the container, and the container list.
        foreach (var path in new[] { $"/api/containers/{id}/settings", $"/api/containers/{id}", "/api/containers/by-name/settings-keys-test", "/api/containers" })
            (await fixture.AdminClient.GetStringAsync(path)).Should().NotContain("con186", path);
        var read = await fixture.AdminClient.GetFromJsonAsync<ContainerSettingsOverrides>($"/api/containers/{id}/settings", JsonOptions);
        read!.Embedding!.OpenAiApiKey.Should().BeNull();
        read.Search!.CrossEncoderApiKey.Should().BeNull();

        // Saving what was read back keeps the stored keys.
        (await fixture.AdminClient.PutAsJsonAsync($"/api/containers/{id}/settings", read)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();
            (await resolver.GetEmbeddingSettingsAsync(id)).OpenAiApiKey.Should().Be("sk-con186-embedding");
            (await resolver.GetSearchSettingsAsync(id)).CrossEncoderApiKey.Should().Be("co-con186-rerank");
        }

        // An empty key clears it.
        read.Search.CrossEncoderApiKey = "";
        (await fixture.AdminClient.PutAsJsonAsync($"/api/containers/{id}/settings", read)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();
            (await resolver.GetEmbeddingSettingsAsync(id)).OpenAiApiKey.Should().Be("sk-con186-embedding");
            (await resolver.GetSearchSettingsAsync(id)).CrossEncoderApiKey.Should().BeNull();
        }

        await fixture.AdminClient.DeleteAsync($"/api/containers/{id}");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("co-con186-replaced", "co-con186-replaced")]
    public async Task UpdateSettingsOverrides_KeyChangedDuringASaveThatLeftItOut_KeepsTheChange(string change, string? expected)
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"settings-race-{(change == "" ? "clear" : "replace")}" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        var id = Guid.Parse(container!.Id);
        using var scope = fixture.Factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        await store.SaveSettingsOverridesAsync(id, new ContainerSettingsOverrides
        {
            Search = new SearchSettings { CrossEncoderProvider = "Cohere", CrossEncoderApiKey = "co-con186-original" },
        });

        // A save that leaves the key out holds the row while it merges; the key changes in the middle of it.
        var leftOut = new ContainerSettingsOverrides { Search = new SearchSettings { CrossEncoderProvider = "Cohere", TopK = 9 } };
        var changed = new ContainerSettingsOverrides { Search = new SearchSettings { CrossEncoderProvider = "Cohere", CrossEncoderApiKey = change } };
        Task<ContainerSettingsOverrides?>? changing = null;
        await store.UpdateSettingsOverridesAsync(id, stored =>
        {
            changing = Task.Run(() => store.UpdateSettingsOverridesAsync(id, changed.KeepingSecretsOf));
            WaitUntilALockIsAwaitedAsync(changing).GetAwaiter().GetResult();
            return leftOut.KeepingSecretsOf(stored);
        });
        await changing!;

        var resolver = scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();
        (await resolver.GetSearchSettingsAsync(id)).CrossEncoderApiKey.Should().Be(expected);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{id}");
    }

    /// <summary>Waits until some session is blocked on a row lock, failing if <paramref name="other"/> finishes without one.</summary>
    private async Task WaitUntilALockIsAwaitedAsync(Task other)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !other.IsCompleted)
        {
            await using var context = await factory.CreateDbContextAsync();
            var waiting = await context.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted AND locktype IN ('tuple', 'transactionid')")
                .ToListAsync();
            if (waiting[0] > 0)
                return;
        }
        await other;
        throw new InvalidOperationException("The other save never waited for the row, so it could land in the middle of this one.");
    }

    [Fact]
    public async Task GetContainerSettings_NonExistentContainer_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var response = await fixture.AdminClient.GetAsync($"/api/containers/{fakeId}/settings");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SaveContainerSettings_NonExistentContainer_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var overrides = new ContainerSettingsOverrides
        {
            Chunking = new ChunkingSettings { Strategy = "FixedSize" }
        };

        var response = await fixture.AdminClient.PutAsJsonAsync(
            $"/api/containers/{fakeId}/settings", overrides);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<SearchSettings> ResolveSavedChoicesAsync(string containerId, SearchSettings global)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        using MemoryCache cache = new(new MemoryCacheOptions());
        return await SourceSearchChoices.ResolveAsync(
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(), cache, containerId, global,
            CancellationToken.None);
    }

    [Fact]
    public async Task SaveContainerSettings_OnlyLinkWeightSent_SearchKeepsTheDeploymentsOtherChoices()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-link-weight-only" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        using StringContent body = new("""{"search":{"linkWeight":0.4}}""", System.Text.Encoding.UTF8, "application/json");

        var putResponse = await fixture.AdminClient.PutAsync($"/api/containers/{container!.Id}/settings", body);

        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        SearchSettings global = new() { EnableLinkSearch = true, UseVectorSearch = false, FusionAlpha = 0.3f, LinkSeeds = 4 };
        SearchSettings effective = await ResolveSavedChoicesAsync(container.Id, global);
        effective.Should().Be(global with { LinkWeight = 0.4f });

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"search":{"topK":"many"}}""")]
    public async Task SaveContainerSettings_BodyIsNotSettingsOverrides_Returns400(string json)
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"settings-bad-body-{Guid.NewGuid():N}"[..40] });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        using StringContent body = new(json, System.Text.Encoding.UTF8, "application/json");

        var response = await fixture.AdminClient.PutAsync($"/api/containers/{container!.Id}/settings", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveSettingsOverrides_TypedSearchOverride_DoesNotNameTheLinkLayersChoices()
    {
        // The file browser saves a typed override with only mode, topK and minimum score set; the rest of the
        // object is defaults it never chose.
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-typed-search" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IContainerStore store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        await store.SaveSettingsOverridesAsync(Guid.Parse(container!.Id),
            new ContainerSettingsOverrides { Search = new SearchSettings { TopK = 25 } },
            [nameof(SearchSettings.Mode), nameof(SearchSettings.TopK), nameof(SearchSettings.MinimumScore)]);

        SearchSettings global = new() { EnableLinkSearch = true, UseVectorSearch = false, FusionAlpha = 0.3f };
        (await ResolveSavedChoicesAsync(container.Id, global)).Should().Be(global);
        (await store.GetSettingsOverridesAsync(Guid.Parse(container.Id)))!.Search!.TopK.Should().Be(25);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveContainerSettings_ResponseSentBackUnchanged_KeepsTheSameSearchChoices()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-round-trip" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        string url = $"/api/containers/{container!.Id}/settings";
        using StringContent body = new("""{"search":{"linkWeight":0.4}}""", System.Text.Encoding.UTF8, "application/json");

        var putResponse = await fixture.AdminClient.PutAsync(url, body);
        string putEcho = await putResponse.Content.ReadAsStringAsync();
        string got = await fixture.AdminClient.GetStringAsync(url);
        using StringContent resend = new(got, System.Text.Encoding.UTF8, "application/json");
        (await fixture.AdminClient.PutAsync(url, resend)).StatusCode.Should().Be(HttpStatusCode.OK);

        SearchNames(putEcho).Should().Equal("linkWeight");
        SearchNames(got).Should().Equal("linkWeight");
        SearchSettings global = new() { EnableLinkSearch = true, UseVectorSearch = false, FusionAlpha = 0.3f };
        (await ResolveSavedChoicesAsync(container.Id, global)).Should().Be(global with { LinkWeight = 0.4f });

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    [Fact]
    public async Task SaveSettingsOverrides_TypedSearchOverrideChoosesADefaultValue_KeepsThatChoice()
    {
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "settings-typed-default-choice" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IContainerStore store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        await store.SaveSettingsOverridesAsync(Guid.Parse(container!.Id),
            new ContainerSettingsOverrides { Search = new SearchSettings { UseVectorSearch = true, LinkWeight = 0.4f } });

        SearchSettings global = new() { EnableLinkSearch = true, UseVectorSearch = false, FusionAlpha = 0.3f };
        SearchSettings effective = await ResolveSavedChoicesAsync(container.Id, global);
        effective.UseVectorSearch.Should().BeTrue("the caller chose vector search, even though true is its default");
        effective.LinkWeight.Should().Be(0.4f);

        await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
    }

    private static List<string> SearchNames(string overridesJson)
    {
        using JsonDocument doc = JsonDocument.Parse(overridesJson);
        return doc.RootElement.GetProperty("search").EnumerateObject().Select(p => p.Name).ToList();
    }

    // DTOs
    private record ContainerDto(string Id, string Name);
}
