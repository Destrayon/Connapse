using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Vectors;
using Connapse.Web.Services;
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
                "awssso" => Results.Ok(await GetSettingsAsync<AwsSsoSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                "azuread" => Results.Ok(await GetSettingsAsync<AzureAdSettings>(categoryLower, settingsStore, serviceProvider, ct)),
                _ => Results.NotFound(new { error = $"Unknown settings category: {category}" })
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
                // Deserialize and save with concrete types to preserve generic T
                // (passing object? erases the type, making JsonSerializer.Serialize<object> fragile)
                var rawJson = settingsJson.GetRawText();
                EmbeddingSettings? embeddingSettings = null;

                switch (categoryLower)
                {
                    case "embedding":
                        embeddingSettings = JsonSerializer.Deserialize<EmbeddingSettings>(rawJson, JsonOptions);
                        if (embeddingSettings != null) await settingsStore.SaveAsync(categoryLower, embeddingSettings, ct);
                        break;
                    case "chunking":
                        var chunking = JsonSerializer.Deserialize<ChunkingSettings>(rawJson, JsonOptions);
                        if (chunking != null) await settingsStore.SaveAsync(categoryLower, chunking, ct);
                        break;
                    case "search":
                        var search = JsonSerializer.Deserialize<SearchSettings>(rawJson, JsonOptions);
                        if (search != null) await settingsStore.SaveAsync(categoryLower, search, ct);
                        break;
                    case "llm":
                        var llm = JsonSerializer.Deserialize<LlmSettings>(rawJson, JsonOptions);
                        if (llm != null) await settingsStore.SaveAsync(categoryLower, llm, ct);
                        break;
                    case "upload":
                        var upload = JsonSerializer.Deserialize<UploadSettings>(rawJson, JsonOptions);
                        if (upload != null) await settingsStore.SaveAsync(categoryLower, upload, ct);
                        break;
                    case "awssso":
                        var awsSso = JsonSerializer.Deserialize<AwsSsoSettings>(rawJson, JsonOptions);
                        if (awsSso != null) await settingsStore.SaveAsync(categoryLower, awsSso, ct);
                        break;
                    case "azuread":
                        var azureAd = JsonSerializer.Deserialize<AzureAdSettings>(rawJson, JsonOptions);
                        if (azureAd != null) await settingsStore.SaveAsync(categoryLower, azureAd, ct);
                        break;
                    default:
                        return Results.NotFound(new { error = $"Unknown settings category: {category}" });
                }

                // When embedding settings change, reconcile partial IVFFlat indexes
                // and detect legacy vectors for cross-model search notification.
                if (embeddingSettings != null)
                {
                    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
                    var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
                    var indexLogger = loggerFactory.CreateLogger("SettingsEndpoints.IndexReconciliation");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var scope = scopeFactory.CreateAsyncScope();
                            var mgr = scope.ServiceProvider.GetRequiredService<VectorColumnManager>();
                            await mgr.EnsureIndexesAsync();
                        }
                        catch (Exception ex)
                        {
                            // Index reconciliation is best-effort; retried on next startup.
                            indexLogger.LogError(ex,
                                "Background index reconciliation failed after embedding settings change");
                        }
                    });

                    // Detect legacy vectors for the new model
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
            [FromServices] VoyageConnectionTester voyageTester,
            [FromServices] MinioConnectionTester minioTester,
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
                    "awssso" => await TestAwsSsoConnection(request.Settings, awsSsoTester, request.TimeoutSeconds, ct),
                    "azuread" => await TestAzureAdConnection(request.Settings, azureAdTester, request.TimeoutSeconds, ct),
                    "crossencoder" => await TestCrossEncoderConnection(request.Settings, teiTester, cohereTester, jinaTester, azureAIFoundryTester, voyageTester, request.TimeoutSeconds, ct),
                    "minio" => await TestMinioConnection(request.Settings, minioTester, request.TimeoutSeconds, ct),
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
            var reindexState = serviceProvider.GetRequiredService<ReindexStateService>();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var reindexLogger = loggerFactory.CreateLogger("SettingsEndpoints.Reindex");

            reindexState.MarkStarted();

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

                    reindexState.MarkCompleted();
                }
                catch (Exception ex)
                {
                    reindexLogger.LogError(ex, "Background reindex operation failed");
                    reindexState.MarkFailed(ex.Message);
                }
            });

            return Results.Ok(new { success = true, message = "Re-embedding started in background" });
        })
        .WithName("TriggerReindex")
        .WithDescription("Start background re-embedding of documents with outdated embedding models");

        // GET /api/settings/reindex/status - Get queue depth as a proxy for re-embedding progress
        group.MapGet("/reindex/status", (
            [FromServices] IIngestionQueue queue,
            [FromServices] ReindexStateService reindexState) =>
        {
            var state = reindexState.Current;
            return Results.Ok(new
            {
                queueDepth = queue.QueueDepth,
                isActive = queue.QueueDepth > 0,
                status = state.Status.ToString(),
                isFailed = state.Status == ReindexStatus.Failed,
                lastError = state.LastError,
                startedAt = state.StartedAt,
                completedAt = state.CompletedAt
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
    private static async Task<ConnectionTestResult> TestMinioConnection(
        JsonElement settingsJson,
        MinioConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<Connapse.Storage.FileSystem.MinioOptions>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
            return ConnectionTestResult.CreateFailure("Invalid MinioOptions");

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestCrossEncoderConnection(
        JsonElement settingsJson,
        TeiConnectionTester teiTester,
        CohereConnectionTester cohereTester,
        JinaConnectionTester jinaTester,
        AzureAIFoundryConnectionTester azureAIFoundryTester,
        VoyageConnectionTester voyageTester,
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
            "Voyage" => voyageTester,
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
