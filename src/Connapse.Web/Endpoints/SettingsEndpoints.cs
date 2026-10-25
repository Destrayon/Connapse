using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Connapse.Web.Endpoints;

public static class SettingsEndpoints
{
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
            .RequireAuthorization("RequireSettingsAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/settings/{category} - Get settings for a category
        // Reads from the database first so callers always see the latest saved value, and falls back to
        // configuration (appsettings.json + env vars) for categories never saved. Secrets are left out.
        group.MapGet("/{category}", async (
            [FromRoute] string category,
            [FromServices] GetSettings get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await get.RunAsync(Rest(http), category, ct);
            return read.Succeeded ? Results.Ok(read.Result!.Settings) : OperationProblems.Result(read);
        })
        .Produces<object>()
        .AddOpenApiOperationTransformer(DescribeCategoryShapesAsync)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("GetSettings")
        .WithDescription("Get settings for a specific category. API keys are never returned.");

        // PUT /api/settings/{category} - Update settings for a category
        group.MapPut("/{category}", async (
            [FromRoute] string category,
            [FromBody] JsonElement settingsJson,
            [FromServices] UpdateSettings update,
            HttpContext http,
            CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), category, settingsJson, UnsentSecrets.Keep, ct);
            if (!updated.Succeeded)
                return OperationProblems.Result(updated);

            var legacy = updated.Result!.LegacyModels;
            return Results.Ok(legacy.Count == 0
                ? new SettingsSavedResponse(true, $"Settings for '{category}' updated successfully")
                : new SettingsSavedResponse(
                    Success: true,
                    Message: $"Settings for '{category}' updated successfully",
                    LegacyVectorsExist: true,
                    LegacyModels: legacy.Select(m => new LegacyEmbeddingModel(m.ModelId, m.VectorCount)).ToList(),
                    LegacyVectorCount: legacy.Sum(m => m.VectorCount)));
        })
        .Produces<SettingsSavedResponse>()
        .AddOpenApiOperationTransformer(DescribeCategoryShapesAsync)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .WithName("UpdateSettings")
        .WithDescription("Update settings for a specific category. An API key left out or null keeps its value; an empty one clears it.");

        // POST /api/settings/test-connection - Test connection with provided settings
        group.MapPost("/test-connection", async (
            [FromBody] TestConnectionRequest request,
            [FromServices] TestConnection test,
            [FromServices] ILoggerFactory loggerFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            try
            {
                var tested = await test.RunAsync(Rest(http),
                    new ConnectionTestRequest(request.Category, request.Settings, request.TimeoutSeconds), ct);
                return tested.Succeeded ? Results.Ok(tested.Result) : OperationProblems.Result(tested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
            [FromServices] ListEmbeddingModels list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), ct);
            return listed.Succeeded
                ? Results.Ok(new GlobalEmbeddingModelsResponse(listed.Result!.CurrentModel, listed.Result.Models, listed.Result.HasLegacyVectors))
                : OperationProblems.Result(listed);
        })
        .Produces<GlobalEmbeddingModelsResponse>()
        .WithName("GetGlobalEmbeddingModels")
        .WithDescription("Get all embedding models with vectors across all containers");

        // POST /api/settings/reindex - Trigger re-embedding of documents with outdated embedding models
        group.MapPost("/reindex", async (
            [FromBody] ReindexTriggerRequest? request,
            [FromServices] StartReindex reindex,
            HttpContext http,
            CancellationToken ct) =>
        {
            var started = await reindex.RunAsync(Rest(http), new ReindexRequest(request?.ContainerId, request?.Force ?? false), ct);
            return started.Succeeded
                ? Results.Ok(new ReindexStartedResponse(true, "Re-embedding started in background"))
                : OperationProblems.Result(started);
        })
        .Produces<ReindexStartedResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .WithName("TriggerReindex")
        .WithDescription("Start re-embedding in the background: without force, only documents whose embedding or chunking settings changed; with force, every document. Cross-model search is turned on meanwhile.");

        // GET /api/settings/reindex/status - Get queue depth as a proxy for re-embedding progress
        group.MapGet("/reindex/status", async (
            [FromServices] GetReindexStatus status,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await status.RunAsync(Rest(http), ct);
            if (!read.Succeeded)
                return OperationProblems.Result(read);
            var (queueDepth, run) = read.Result!;
            return Results.Ok(new ReindexStatusResponse(
                queueDepth,
                queueDepth > 0,
                run.Status.ToString(),
                run.Status == ReindexStatus.Failed,
                run.LastError,
                run.StartedAt,
                run.CompletedAt));
        })
        .Produces<ReindexStatusResponse>()
        .WithName("GetReindexStatus")
        .WithDescription("Get current re-embedding/reindex queue status");

        var v1 = app.MapGroup("/api/v1/settings").WithTags("Settings")
            .RequireAuthorization("RequireSettingsAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // DELETE /api/v1/settings/{category} - Reset a category to what configuration gives it (#922)
        v1.MapDelete("/{category}", async (
            [FromRoute] string category,
            [FromServices] ResetSettings reset,
            HttpContext http,
            CancellationToken ct) =>
        {
            var done = await reset.RunAsync(Rest(http), category, ct);
            return done.Succeeded ? Results.NoContent() : OperationProblems.Result(done);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("settings.reset")
        .WithDescription("Reset a settings category to its configured defaults by removing what is stored for it.");

        // POST /api/v1/settings/summaries/clear - Clear every container's summaries (#950)
        v1.MapPost("/summaries/clear", async (
            [FromServices] ClearAllSummaries clear,
            HttpContext http,
            CancellationToken ct) =>
        {
            var cleared = await clear.RunAsync(Rest(http), ct);
            return cleared.Succeeded ? Results.Ok(cleared.Result) : OperationProblems.Result(cleared);
        })
        .Produces<SummariesCleared>()
        .WithName("settings.summaries.clear")
        .WithDescription("Clear every container's summary and every document summary; the next sweep writes them again. Embeddings, chunks and documents are kept.");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    /// <summary>The settings type each category reads and writes.</summary>
    private static readonly (string Category, Type Type)[] CategoryShapes =
        SettingsCategories.All.Select(c => (c.Name, c.Type)).ToArray();

    /// <summary>
    /// GET's answer and PUT's body are the category's settings: anyOf every category's shape, each a component, since
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
}

// Request DTOs
public record TestConnectionRequest(
    string Category,
    JsonElement Settings,
    int? TimeoutSeconds = null);

public record ReindexTriggerRequest(
    string? ContainerId = null,
    bool Force = false);

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
