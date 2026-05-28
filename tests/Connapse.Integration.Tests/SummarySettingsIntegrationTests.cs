using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests for SummarySettings round-trip — both global (DB via ISettingsStore)
/// and per-container (HTTP via /api/containers/{id}/settings).
///
/// Note: as of #330 the global /api/settings/{category} REST endpoint does NOT include
/// a "summary" case in its switch — only the Blazor UI and ISettingsStore touch global
/// summary settings today. The global test exercises the service layer directly to prove
/// persistence. A separate test pins the current 404 behaviour so the gap is visible if
/// someone later wires the HTTP endpoint without removing the assertion.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SummarySettingsIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ── Global SummarySettings — service-layer round-trip ─────────────────

    [Fact]
    public async Task SaveAsync_ThenGetAsync_GlobalSummarySettings_RoundTrips()
    {
        // Arrange — ISettingsStore is the source of truth for global settings;
        // the global HTTP endpoint doesn't expose "summary" yet (see class doc).
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        var original = await settingsStore.GetAsync<SummarySettings>("Summary");
        try
        {
            var saved = new SummarySettings
            {
                Enabled = true,
                LlmProvider = "Ollama",
                LlmModel = "qwen3:14b",
                PerDocSystemPrompt = "Test per-doc prompt.",
                ContainerRollupSystemPrompt = "Test container prompt.",
                MaxInputTokens = 3000
            };

            // Act
            await settingsStore.SaveAsync("Summary", saved);
            var loaded = await settingsStore.GetAsync<SummarySettings>("Summary");

            // Assert
            loaded.Should().NotBeNull();
            loaded!.Enabled.Should().BeTrue();
            loaded.LlmProvider.Should().Be("Ollama");
            loaded.LlmModel.Should().Be("qwen3:14b");
            loaded.PerDocSystemPrompt.Should().Be("Test per-doc prompt.");
            loaded.ContainerRollupSystemPrompt.Should().Be("Test container prompt.");
            loaded.MaxInputTokens.Should().Be(3000);
        }
        finally
        {
            // Restore (or reset to defaults if nothing was there originally) so we don't
            // leak state into sibling tests that share the fixture.
            if (original is null)
                await settingsStore.ResetAsync("Summary");
            else
                await settingsStore.SaveAsync("Summary", original);
        }
    }

    [Fact]
    public async Task SaveAsync_NullOptionalFields_PreservesNulls()
    {
        // Arrange
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        var original = await settingsStore.GetAsync<SummarySettings>("Summary");
        try
        {
            // Only Enabled is set — every other field is null, exercising the
            // "inherit from LLM settings / fall back to defaults" path.
            var saved = new SummarySettings { Enabled = false };

            // Act
            await settingsStore.SaveAsync("Summary", saved);
            var loaded = await settingsStore.GetAsync<SummarySettings>("Summary");

            // Assert
            loaded.Should().NotBeNull();
            loaded!.Enabled.Should().BeFalse();
            loaded.LlmProvider.Should().BeNull();
            loaded.LlmModel.Should().BeNull();
            loaded.PerDocSystemPrompt.Should().BeNull();
            loaded.ContainerRollupSystemPrompt.Should().BeNull();
            loaded.MaxInputTokens.Should().BeNull();
        }
        finally
        {
            if (original is null)
                await settingsStore.ResetAsync("Summary");
            else
                await settingsStore.SaveAsync("Summary", original);
        }
    }

    // ── Global SummarySettings — pins the missing HTTP endpoint ───────────

    [Fact]
    public async Task GetSettings_SummaryCategory_Returns404_BecauseHttpEndpointNotWired()
    {
        // Documents the current gap: SettingsEndpoints.cs has no "summary" case in its
        // GET/PUT switch — global summary settings flow through the Blazor UI and
        // ISettingsStore only. If this test ever fails with 200, the HTTP endpoint
        // has been wired up and this test should be replaced with a real round-trip.
        var response = await fixture.AdminClient.GetAsync("/api/settings/summary");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Per-container SummarySettings — HTTP round-trip ───────────────────

    [Fact]
    public async Task SaveContainerSettings_SummaryOverride_PersistsAndRetrieves()
    {
        // Arrange — create a fresh container so this test is self-contained.
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "summary-settings-override-test" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);
        container.Should().NotBeNull();

        try
        {
            var overrides = new ContainerSettingsOverrides
            {
                Summary = new SummarySettings
                {
                    Enabled = true,
                    LlmProvider = "Anthropic",
                    LlmModel = "claude-haiku-4-5",
                    PerDocSystemPrompt = "Container-scoped per-doc prompt.",
                    ContainerRollupSystemPrompt = "Container-scoped rollup prompt.",
                    MaxInputTokens = 2000
                }
            };

            // Act — PUT then GET the per-container settings.
            var putResponse = await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{container!.Id}/settings", overrides);
            putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var getResponse = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/settings");
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

            // Assert — only the Summary slot should be populated.
            retrieved.Should().NotBeNull();
            retrieved!.Summary.Should().NotBeNull();
            retrieved.Summary!.Enabled.Should().BeTrue();
            retrieved.Summary.LlmProvider.Should().Be("Anthropic");
            retrieved.Summary.LlmModel.Should().Be("claude-haiku-4-5");
            retrieved.Summary.PerDocSystemPrompt.Should().Be("Container-scoped per-doc prompt.");
            retrieved.Summary.ContainerRollupSystemPrompt.Should().Be("Container-scoped rollup prompt.");
            retrieved.Summary.MaxInputTokens.Should().Be(2000);

            retrieved.Chunking.Should().BeNull("only summary was overridden");
            retrieved.Embedding.Should().BeNull("only summary was overridden");
            retrieved.Search.Should().BeNull("only summary was overridden");
            retrieved.Upload.Should().BeNull("only summary was overridden");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task SaveContainerSettings_SummaryOverride_ResetToNull_ClearsOverride()
    {
        // Arrange
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "summary-settings-reset-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            // Set a summary override.
            var withOverride = new ContainerSettingsOverrides
            {
                Summary = new SummarySettings { Enabled = true, LlmModel = "to-be-cleared" }
            };
            await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{container!.Id}/settings", withOverride);

            // Act — clear it by writing empty overrides.
            var cleared = new ContainerSettingsOverrides();
            var putResponse = await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{container.Id}/settings", cleared);
            putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // Assert — Summary slot is back to null.
            var getResponse = await fixture.AdminClient.GetAsync(
                $"/api/containers/{container.Id}/settings");
            var retrieved = await getResponse.Content.ReadFromJsonAsync<ContainerSettingsOverrides>(JsonOptions);

            retrieved.Should().NotBeNull();
            retrieved!.Summary.Should().BeNull("override was cleared");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    // ── ContainerSummaryMethod resolution (G+C) ───────────────────────────

    [Fact]
    public async Task GetSummarySettingsAsync_ContainerSummaryMethodOverride_TakesPrecedence()
    {
        // Arrange — global says summary-clustering, per-container override says document-clustering.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();

        var originalGlobal = await settingsStore.GetAsync<SummarySettings>("Summary");
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "method-override-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            await settingsStore.SaveAsync("Summary", new SummarySettings
            {
                Enabled = true,
                ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
            });

            var overrides = new ContainerSettingsOverrides
            {
                Summary = new SummarySettings
                {
                    Enabled = true,
                    ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
                }
            };
            await fixture.AdminClient.PutAsJsonAsync(
                $"/api/containers/{container!.Id}/settings", overrides);

            // Act
            var resolved = await resolver.GetSummarySettingsAsync(
                Guid.Parse(container.Id), CancellationToken.None);

            // Assert — per-container override wins.
            resolved.ContainerSummaryMethod.Should().Be(SummaryStrategy.DocumentClustering);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
            if (originalGlobal is null)
                await settingsStore.ResetAsync("Summary");
            else
                await settingsStore.SaveAsync("Summary", originalGlobal);
        }
    }

    [Fact]
    public async Task GetSummarySettingsAsync_NoOverride_UsesDocumentClusteringDefault()
    {
        // Arrange — no global SummarySettings, no per-container override.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IContainerSettingsResolver>();

        var originalGlobal = await settingsStore.GetAsync<SummarySettings>("Summary");
        await settingsStore.ResetAsync("Summary");

        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = "method-default-test" });
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            // Act
            var resolved = await resolver.GetSummarySettingsAsync(
                Guid.Parse(container!.Id), CancellationToken.None);

            // Assert — record-default applies when both layers are absent.
            resolved.ContainerSummaryMethod.Should().Be(SummaryStrategy.DocumentClustering);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
            if (originalGlobal is not null)
                await settingsStore.SaveAsync("Summary", originalGlobal);
        }
    }

    // DTOs

    private record ContainerDto(string Id, string Name);
}
