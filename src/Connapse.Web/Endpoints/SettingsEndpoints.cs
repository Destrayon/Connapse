using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Vectors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class SettingsEndpoints
{
    // JSON options for deserializing settings with case-insensitive property names
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings")
            .RequireAuthorization("RequireAdmin");

        // GET /api/settings/{category} - Get settings for a category
        // Reads from the database first so callers always see the latest saved value.
        // Falls back to IOptionsMonitor (appsettings.json + env vars) for categories
        // that have never been persisted to the database.
        group.MapGet("/{category}", async (
            [FromRoute] string category,
            [FromServices] ISettingsStore settingsStore,
            [FromServices] IServiceProvider serviceProvider,
            CancellationToken ct) =>
        {
            var categoryLower = category.ToLowerInvariant();

            return categoryLower switch
            {
                "embedding" => Results.Ok(await GetSettingsAsync<EmbeddingSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "chunking" => Results.Ok(await GetSettingsAsync<ChunkingSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "search" => Results.Ok(await GetSettingsAsync<SearchSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "llm" => Results.Ok(await GetSettingsAsync<LlmSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "upload" => Results.Ok(await GetSettingsAsync<UploadSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "websearch" => Results.Ok(await GetSettingsAsync<WebSearchSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "storage" => Results.Ok(await GetSettingsAsync<StorageSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "awssso" => Results.Ok(await GetSettingsAsync<AwsSsoSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "azuread" => Results.Ok(await GetSettingsAsync<AzureAdSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                _ => Results.BadRequest(new { error = $"Unknown category: {category}" })
            };
        })
        .WithName("GetSettings")
        .WithDescription("Get settings for a specific category");

        // PUT /api/settings/{category} - Update settings for a category
        group.MapPut("/{category}", async (
            [FromRoute] string category,
            [FromBody] JsonElement settingsJson,
            [FromServices] ISettingsStore settingsStore,
            [FromServices] IServiceProvider serviceProvider,
            CancellationToken ct) =>
        {
            var categoryLower = category.ToLowerInvariant();

            try
            {
                // Deserialize JSON to the appropriate settings type
                object? settings = categoryLower switch
                {
                    "embedding" => JsonSerializer.Deserialize<EmbeddingSettings>(settingsJson.GetRawText(), JsonOptions),
                    "chunking" => JsonSerializer.Deserialize<ChunkingSettings>(settingsJson.GetRawText(), JsonOptions),
                    "search" => JsonSerializer.Deserialize<SearchSettings>(settingsJson.GetRawText(), JsonOptions),
                    "llm" => JsonSerializer.Deserialize<LlmSettings>(settingsJson.GetRawText(), JsonOptions),
                    "upload" => JsonSerializer.Deserialize<UploadSettings>(settingsJson.GetRawText(), JsonOptions),
                    "websearch" => JsonSerializer.Deserialize<WebSearchSettings>(settingsJson.GetRawText(), JsonOptions),
                    "storage" => JsonSerializer.Deserialize<StorageSettings>(settingsJson.GetRawText(), JsonOptions),
                    "awssso" => JsonSerializer.Deserialize<AwsSsoSettings>(settingsJson.GetRawText(), JsonOptions),
                    "azuread" => JsonSerializer.Deserialize<AzureAdSettings>(settingsJson.GetRawText(), JsonOptions),
                    _ => null
                };

                if (settings == null)
                {
                    return Results.BadRequest(new { error = $"Unknown category or invalid settings: {category}" });
                }

                // Save to database
                await settingsStore.SaveAsync(categoryLower, settings, ct);

                // When embedding settings change, reconcile partial IVFFlat indexes
                // and detect legacy vectors for cross-model search notification.
                if (categoryLower == "embedding")
                {
                    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = scopeFactory.CreateAsyncScope();
                            var mgr = scope.ServiceProvider.GetRequiredService<VectorColumnManager>();
                            await mgr.EnsureIndexesAsync();
                        }
                        catch { /* Index reconciliation is best-effort; retried on next startup */ }
                    });

                    // Detect legacy vectors for the new model
                    var embeddingSettings = (EmbeddingSettings)settings;
                    var discovery = serviceProvider.GetRequiredService<VectorModelDiscovery>();
                    var models = await discovery.GetModelsAsync(containerId: null, ct);
                    var legacyModels = models
                        .Where(m => !string.Equals(m.ModelId, embeddingSettings.Model, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (legacyModels.Count > 0)
                    {
                        return Results.Ok(new
                        {
                            success = true,
                            message = $"Settings for '{category}' updated successfully",
                            legacyVectorsExist = true,
                            legacyModels = legacyModels.Select(m => new { m.ModelId, m.VectorCount }),
                            legacyVectorCount = legacyModels.Sum(m => m.VectorCount)
                        });
                    }
                }

                return Results.Ok(new { success = true, message = $"Settings for '{category}' updated successfully" });
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Failed to update settings");
            }
        })
        .WithName("UpdateSettings")
        .WithDescription("Update settings for a specific category");

        // POST /api/settings/test-connection - Test connection with provided settings
        group.MapPost("/test-connection", async (
            [FromBody] TestConnectionRequest request,
            [FromServices] OllamaConnectionTester ollamaTester,
            [FromServices] MinioConnectionTester minioTester,
            [FromServices] AwsSsoConnectionTester awsSsoTester,
            [FromServices] AzureAdConnectionTester azureAdTester,
            [FromServices] OpenAiConnectionTester openAiTester,
            [FromServices] AzureOpenAiConnectionTester azureOpenAiTester,
            [FromServices] OpenAiLlmConnectionTester openAiLlmTester,
            [FromServices] AzureOpenAiLlmConnectionTester azureOpenAiLlmTester,
            [FromServices] AnthropicConnectionTester anthropicTester,
            [FromServices] TeiConnectionTester teiTester,
            [FromServices] CohereConnectionTester cohereTester,
            [FromServices] JinaConnectionTester jinaTester,
            [FromServices] AzureAIFoundryConnectionTester azureAIFoundryTester,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Category))
            {
                return Results.BadRequest(new { error = "Category is required" });
            }

            if (request.Settings.ValueKind == JsonValueKind.Undefined || request.Settings.ValueKind == JsonValueKind.Null)
            {
                return Results.BadRequest(new { error = "Settings are required" });
            }

            var categoryLower = request.Category.ToLowerInvariant();

            try
            {
                // Deserialize settings and select appropriate tester
                ConnectionTestResult result = categoryLower switch
                {
                    "embedding" => await TestEmbeddingConnection(request.Settings, ollamaTester, openAiTester, azureOpenAiTester, request.TimeoutSeconds, ct),
                    "llm" => await TestLlmConnection(request.Settings, ollamaTester, openAiLlmTester, azureOpenAiLlmTester, anthropicTester, request.TimeoutSeconds, ct),
                    "storage" => await TestStorageConnection(request.Settings, minioTester, request.TimeoutSeconds, ct),
                    "awssso" => await TestAwsSsoConnection(request.Settings, awsSsoTester, request.TimeoutSeconds, ct),
                    "azuread" => await TestAzureAdConnection(request.Settings, azureAdTester, request.TimeoutSeconds, ct),
                    "crossencoder" => await TestCrossEncoderConnection(request.Settings, teiTester, cohereTester, jinaTester, azureAIFoundryTester, request.TimeoutSeconds, ct),
                    _ => ConnectionTestResult.CreateFailure($"Category '{request.Category}' does not support connection testing")
                };

                return Results.Ok(result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid settings JSON: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: 500,
                    title: "Connection test failed");
            }
        })
        .WithName("TestConnection")
        .WithDescription("Test connectivity to external services (Ollama, MinIO, etc.)");

        // GET /api/settings/embedding-models - Get all embedding models with vectors (global)
        group.MapGet("/embedding-models", async (
            [FromServices] VectorModelDiscovery modelDiscovery,
            [FromServices] IOptionsMonitor<EmbeddingSettings> embeddingSettings,
            CancellationToken ct) =>
        {
            var models = await modelDiscovery.GetModelsAsync(containerId: null, ct);
            var currentModel = embeddingSettings.CurrentValue.Model;

            return Results.Ok(new
            {
                currentModel,
                models = models.Select(m => new
                {
                    m.ModelId,
                    m.Dimensions,
                    m.VectorCount,
                    isCurrent = string.Equals(m.ModelId, currentModel, StringComparison.OrdinalIgnoreCase)
                }),
                hasLegacyVectors = models.Any(m =>
                    !string.Equals(m.ModelId, currentModel, StringComparison.OrdinalIgnoreCase))
            });
        })
        .WithName("GetGlobalEmbeddingModels")
        .WithDescription("Get all embedding models with vectors across all containers");

        // POST /api/settings/reindex - Trigger re-embedding of documents with outdated embedding models
        group.MapPost("/reindex", async (
            [FromBody] ReindexTriggerRequest? request,
            [FromServices] IServiceProvider serviceProvider,
            CancellationToken ct) =>
        {
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

            // Run reindex asynchronously — don't block the HTTP request
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reindexService = scope.ServiceProvider.GetRequiredService<IReindexService>();

                    // Also auto-enable cross-model search during re-embedding
                    var settingsStore = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
                    var searchSettings = await settingsStore.GetAsync<SearchSettings>("search", CancellationToken.None)
                        ?? new SearchSettings();
                    if (!searchSettings.EnableCrossModelSearch)
                    {
                        searchSettings.EnableCrossModelSearch = true;
                        await settingsStore.SaveAsync("search", searchSettings, CancellationToken.None);
                    }

                    await reindexService.ReindexAsync(new ReindexOptions
                    {
                        ContainerId = request?.ContainerId,
                        DetectSettingsChanges = true
                    }, CancellationToken.None);
                }
                catch { /* Reindex is best-effort; errors logged by ReindexService */ }
            });

            return Results.Ok(new { success = true, message = "Re-embedding started in background" });
        })
        .WithName("TriggerReindex")
        .WithDescription("Start background re-embedding of documents with outdated embedding models");

        // GET /api/settings/reindex/status - Get queue depth as a proxy for re-embedding progress
        group.MapGet("/reindex/status", (
            [FromServices] IIngestionQueue queue) =>
        {
            return Results.Ok(new
            {
                queueDepth = queue.QueueDepth,
                isActive = queue.QueueDepth > 0
            });
        })
        .WithName("GetReindexStatus")
        .WithDescription("Get current re-embedding/reindex queue status");

        return app;
    }

    /// <summary>
    /// Returns the persisted DB value for <typeparamref name="T"/> when one exists,
    /// otherwise falls back to the IOptionsMonitor in-memory value (from appsettings / env vars).
    /// Reading from the DB ensures the endpoint always reflects the latest saved settings,
    /// which is particularly important for integration tests where IOptionsMonitor live-reload
    /// may not propagate correctly inside WebApplicationFactory.
    /// </summary>
    private static async Task<T> GetSettingsAsync<T>(
        string category,
        ISettingsStore settingsStore,
        IServiceProvider serviceProvider,
        CancellationToken ct) where T : class
    {
        var stored = await settingsStore.GetAsync<T>(category, ct);
        return stored ?? serviceProvider.GetRequiredService<IOptionsMonitor<T>>().CurrentValue;
    }

    private static async Task<ConnectionTestResult> TestEmbeddingConnection(
        JsonElement settingsJson,
        OllamaConnectionTester ollamaTester,
        OpenAiConnectionTester openAiTester,
        AzureOpenAiConnectionTester azureOpenAiTester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<EmbeddingSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid EmbeddingSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;

        IConnectionTester tester = settings.Provider switch
        {
            "OpenAI" => openAiTester,
            "AzureOpenAI" => azureOpenAiTester,
            _ => ollamaTester
        };

        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestLlmConnection(
        JsonElement settingsJson,
        OllamaConnectionTester ollamaTester,
        OpenAiLlmConnectionTester openAiLlmTester,
        AzureOpenAiLlmConnectionTester azureOpenAiLlmTester,
        AnthropicConnectionTester anthropicTester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<LlmSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid LlmSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;

        IConnectionTester tester = settings.Provider switch
        {
            "OpenAI" => openAiLlmTester,
            "AzureOpenAI" => azureOpenAiLlmTester,
            "Anthropic" => anthropicTester,
            _ => ollamaTester
        };

        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestStorageConnection(
        JsonElement settingsJson,
        MinioConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<StorageSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid StorageSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestAwsSsoConnection(
        JsonElement settingsJson,
        AwsSsoConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<AwsSsoSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid AwsSsoSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestAzureAdConnection(
        JsonElement settingsJson,
        AzureAdConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<AzureAdSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid AzureAdSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        return await tester.TestConnectionAsync(settings, timeout, ct);
    }
    private static async Task<ConnectionTestResult> TestCrossEncoderConnection(
        JsonElement settingsJson,
        TeiConnectionTester teiTester,
        CohereConnectionTester cohereTester,
        JinaConnectionTester jinaTester,
        AzureAIFoundryConnectionTester azureAIFoundryTester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<SearchSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
            return ConnectionTestResult.CreateFailure("Invalid SearchSettings");

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;

        IConnectionTester tester = settings.CrossEncoderProvider switch
        {
            "Cohere" => cohereTester,
            "Jina" => jinaTester,
            "AzureAIFoundry" => azureAIFoundryTester,
            _ => teiTester
        };

        return await tester.TestConnectionAsync(settings, timeout, ct);
    }
}

// Request DTOs
public record TestConnectionRequest(
    string Category,
    JsonElement Settings,
    int? TimeoutSeconds = null);

public record ReindexTriggerRequest(
    string? ContainerId = null);
