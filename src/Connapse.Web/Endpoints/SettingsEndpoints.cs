using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Vectors;
using Connapse.Web.Problems;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

public static class SettingsEndpoints
{
    // JSON options for deserializing settings with case-insensitive property names
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Maps and configures settings-related HTTP endpoints under "/api/settings" and applies the "RequireAdmin" authorization policy.
    /// </summary>
    /// <param name="app">The endpoint route builder to attach the settings routes to.</param>
    /// <summary>
    /// Adds authenticated HTTP endpoints for managing and testing application settings under the "/api/settings" route.
    /// </summary>
    /// <param name="app">The endpoint route builder to extend with the settings endpoints.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> with the settings endpoints mapped.</returns>
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings")
            .RequireAuthorization("RequireAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
                _ => ApiProblem.NotFound($"Unknown settings category: {category}")
            };
        })
        .Produces<object>()
        .AddOpenApiOperationTransformer(DescribeCategoryShapesAsync)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetSettings")
        .WithDescription("Get settings for a specific category");

        // PUT /api/settings/{category} - Update settings for a category
        group.MapPut("/{category}", async (
            [FromRoute] string category,
            [FromBody] JsonElement settingsJson,
            [FromServices] ISettingsStore settingsStore,
            [FromServices] IServiceProvider serviceProvider,
            [FromServices] ILoggerFactory loggerFactory,
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
                    default:
                        return ApiProblem.NotFound($"Unknown settings category: {category}");
                }

                // When embedding settings change, reconcile partial IVFFlat indexes
                // and detect legacy vectors for cross-model search notification.
                if (embeddingSettings != null)
                {
                    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
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
                        .Where(m => !string.Equals(m.ModelId, EmbeddingIdentity.For(embeddingSettings), StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (legacyModels.Count > 0)
                    {
                        return Results.Ok(new SettingsSavedResponse(
                            Success: true,
                            Message: $"Settings for '{category}' updated successfully",
                            LegacyVectorsExist: true,
                            LegacyModels: legacyModels.Select(m => new LegacyEmbeddingModel(m.ModelId, m.VectorCount)).ToList(),
                            LegacyVectorCount: legacyModels.Sum(m => m.VectorCount)));
                    }
                }

                return Results.Ok(new SettingsSavedResponse(true, $"Settings for '{category}' updated successfully"));
            }
            catch (JsonException ex)
            {
                return ApiProblem.BadRequest($"Invalid JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                // The exception's text can carry a connection string or an internal id; it stays in the log.
                loggerFactory.CreateLogger("SettingsEndpoints").LogError(ex, "Updating settings failed");
                return Results.Problem(
                    statusCode: 500,
                    title: "Failed to update settings");
            }
        })
        .Produces<SettingsSavedResponse>()
        .AddOpenApiOperationTransformer(DescribeCategoryShapesAsync)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithName("UpdateSettings")
        .WithDescription("Update settings for a specific category");

        // POST /api/settings/test-connection - Test connection with provided settings
        group.MapPost("/test-connection", async (
            [FromBody] TestConnectionRequest request,
            [FromServices] OllamaConnectionTester ollamaTester,
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
            [FromServices] ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Category))
            {
                return ApiProblem.BadRequest("Category is required");
            }

            if (request.Settings.ValueKind == JsonValueKind.Undefined || request.Settings.ValueKind == JsonValueKind.Null)
            {
                return ApiProblem.BadRequest("Settings are required");
            }

            var categoryLower = request.Category.ToLowerInvariant();

            try
            {
                // Deserialize settings and select appropriate tester
                ConnectionTestResult result = categoryLower switch
                {
                    "embedding" => await TestEmbeddingConnection(request.Settings, ollamaTester, openAiTester, azureOpenAiTester, request.TimeoutSeconds, ct),
                    "llm" => await TestLlmConnection(request.Settings, ollamaTester, openAiLlmTester, azureOpenAiLlmTester, anthropicTester, request.TimeoutSeconds, ct),
                    "crossencoder" => await TestCrossEncoderConnection(request.Settings, teiTester, cohereTester, jinaTester, azureAIFoundryTester, voyageTester, request.TimeoutSeconds, ct),
                    "minio" => await TestMinioConnection(request.Settings, minioTester, request.TimeoutSeconds, ct),
                    _ => ConnectionTestResult.CreateFailure($"Category '{request.Category}' does not support connection testing")
                };

                return Results.Ok(result);
            }
            catch (JsonException ex)
            {
                return ApiProblem.BadRequest($"Invalid settings JSON: {ex.Message}");
            }
            catch (Exception ex)
            {
                // The exception's text can carry a credential or an internal id; it stays in the log.
                loggerFactory.CreateLogger("SettingsEndpoints").LogError(ex, "Connection test failed");
                return Results.Problem(
                    statusCode: 500,
                    title: "Connection test failed");
            }
        })
        .Produces<ConnectionTestResult>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithName("TestConnection")
        .WithDescription("Test connectivity to external services (Ollama, MinIO, etc.)");

        // GET /api/settings/embedding-models - Get all embedding models with vectors (global)
        group.MapGet("/embedding-models", async (
            [FromServices] VectorModelDiscovery modelDiscovery,
            [FromServices] IOptionsMonitor<EmbeddingSettings> embeddingSettings,
            CancellationToken ct) =>
        {
            var models = await modelDiscovery.GetModelsAsync(containerId: null, ct);
            var currentModel = EmbeddingIdentity.For(embeddingSettings.CurrentValue);

            return Results.Ok(new GlobalEmbeddingModelsResponse(
                currentModel,
                EmbeddingModelUsage.From(models, currentModel),
                models.Any(m => !string.Equals(m.ModelId, currentModel, StringComparison.OrdinalIgnoreCase))));
        })
        .Produces<GlobalEmbeddingModelsResponse>()
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

            return Results.Ok(new ReindexStartedResponse(true, "Re-embedding started in background"));
        })
        .Produces<ReindexStartedResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("TriggerReindex")
        .WithDescription("Start background re-embedding of documents with outdated embedding models");

        // GET /api/settings/reindex/status - Get queue depth as a proxy for re-embedding progress
        group.MapGet("/reindex/status", async (
            [FromServices] IIngestionQueue queue,
            [FromServices] ReindexStateService reindexState,
            CancellationToken ct) =>
        {
            var state = reindexState.Current;
            int queueDepth = await queue.GetQueueDepthAsync(ct);
            return Results.Ok(new ReindexStatusResponse(
                queueDepth,
                queueDepth > 0,
                state.Status.ToString(),
                state.Status == ReindexStatus.Failed,
                state.LastError,
                state.StartedAt,
                state.CompletedAt));
        })
        .Produces<ReindexStatusResponse>()
        .WithName("GetReindexStatus")
        .WithDescription("Get current re-embedding/reindex queue status");

        return app;
    }

    /// <summary>The settings type each category reads and writes.</summary>
    private static readonly (string Category, Type Type)[] CategoryShapes =
    [
        ("embedding", typeof(EmbeddingSettings)),
        ("chunking", typeof(ChunkingSettings)),
        ("search", typeof(SearchSettings)),
        ("llm", typeof(LlmSettings)),
        ("upload", typeof(UploadSettings)),
    ];

    /// <summary>
    /// GET's answer and PUT's body are the category's settings: anyOf the five shapes, each a component, since
    /// they share optional properties and a body can match more than one. The description names which is whose.
    /// </summary>
    private static async Task DescribeCategoryShapesAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        var shapes = new OpenApiSchema
        {
            AnyOf = [],
            Description = "The category's settings: "
                + string.Join(", ", CategoryShapes.Select(c => $"{c.Category}: {c.Type.Name}")) + ".",
        };
        foreach (var (_, type) in CategoryShapes)
        {
            var schema = await context.GetOrCreateSchemaAsync(type, null, ct);
            context.Document!.AddComponent(type.Name, schema);
            shapes.AnyOf.Add(new OpenApiSchemaReference(type.Name, context.Document));
        }

        if (operation.RequestBody?.Content is { } body && body.TryGetValue("application/json", out var request))
            request.Schema = shapes;
        if (context.Description.HttpMethod == HttpMethods.Get
            && operation.Responses?.TryGetValue("200", out var ok) == true && ok.Content is { } content
            && content.TryGetValue("application/json", out var response))
            response.Schema = shapes;
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

    /// <summary>
    /// Tests connectivity to a MinIO-compatible storage endpoint using the supplied settings JSON.
    /// </summary>
    /// <param name="settingsJson">A JSON element containing serialized Connapse.Storage.FileSystem.MinioOptions.</param>
    /// <param name="tester">The service that performs the MinIO connection test.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds for the test; null means no timeout.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>
    /// A <see cref="ConnectionTestResult"/> describing whether the connection succeeded and any diagnostic information; returns a failure result if the settings JSON is invalid.
    /// </returns>
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

    /// <summary>
    /// Tests the cross-encoder connection described by the supplied SearchSettings JSON, using the provider specified in SearchSettings.CrossEncoderProvider.
    /// </summary>
    /// <param name="settingsJson">A JSON element containing SearchSettings to use for the connection test.</param>
    /// <param name="timeoutSeconds">Optional timeout in seconds for the connection test; null means no specific timeout.</param>
    /// <param name="ct">Cancellation token to cancel the test operation.</param>
    /// <summary>
    /// Tests connectivity for a cross-encoder provider using the provided SearchSettings JSON.
    /// </summary>
    /// <param name="settingsJson">Raw JSON representing the <see cref="SearchSettings"/> to use for the test.</param>
    /// <param name="timeoutSeconds">Optional maximum time in seconds to allow the test to run; null to use the tester's default.</param>
    /// <param name="ct">A cancellation token to cancel the connection test.</param>
    /// <returns>A <see cref="ConnectionTestResult"/> describing success or failure of the connection test.</returns>
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

// Response DTOs
/// <summary>A settings save. The legacy fields are present only when vectors from other embedding models remain.</summary>
public record SettingsSavedResponse(
    bool Success,
    string Message,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    bool? LegacyVectorsExist = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LegacyEmbeddingModel>? LegacyModels = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    long? LegacyVectorCount = null);

public record LegacyEmbeddingModel(string ModelId, long VectorCount);

public record GlobalEmbeddingModelsResponse(string CurrentModel, IReadOnlyList<EmbeddingModelUsage> Models, bool HasLegacyVectors);

public record ReindexStartedResponse(bool Success, string Message);

public record ReindexStatusResponse(
    int QueueDepth,
    bool IsActive,
    string Status,
    bool IsFailed,
    string? LastError,
    DateTime? StartedAt,
    DateTime? CompletedAt);
