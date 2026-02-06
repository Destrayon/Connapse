using System.Text.Json;
using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Storage.ConnectionTesters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIKnowledge.Web.Endpoints;

public static class SettingsEndpoints
{
    // JSON options for deserializing settings with case-insensitive property names
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        // GET /api/settings/{category} - Get settings for a category
        group.MapGet("/{category}", async (
            [FromRoute] string category,
            [FromServices] IServiceProvider serviceProvider) =>
        {
            var categoryLower = category.ToLowerInvariant();

            return categoryLower switch
            {
                "embedding" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue),
                "chunking" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<ChunkingSettings>>().CurrentValue),
                "search" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<SearchSettings>>().CurrentValue),
                "llm" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<LlmSettings>>().CurrentValue),
                "upload" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<UploadSettings>>().CurrentValue),
                "websearch" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<WebSearchSettings>>().CurrentValue),
                "storage" => Results.Ok(serviceProvider.GetRequiredService<IOptionsMonitor<StorageSettings>>().CurrentValue),
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
                    _ => null
                };

                if (settings == null)
                {
                    return Results.BadRequest(new { error = $"Unknown category or invalid settings: {category}" });
                }

                // Save to database
                await settingsStore.SaveAsync(categoryLower, settings, ct);

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
                    "embedding" => await TestEmbeddingConnection(request.Settings, ollamaTester, request.TimeoutSeconds, ct),
                    "llm" => await TestLlmConnection(request.Settings, ollamaTester, request.TimeoutSeconds, ct),
                    "storage" => await TestStorageConnection(request.Settings, minioTester, request.TimeoutSeconds, ct),
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

        return app;
    }

    private static async Task<ConnectionTestResult> TestEmbeddingConnection(
        JsonElement settingsJson,
        OllamaConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<EmbeddingSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid EmbeddingSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
        return await tester.TestConnectionAsync(settings, timeout, ct);
    }

    private static async Task<ConnectionTestResult> TestLlmConnection(
        JsonElement settingsJson,
        OllamaConnectionTester tester,
        int? timeoutSeconds,
        CancellationToken ct)
    {
        var settings = JsonSerializer.Deserialize<LlmSettings>(settingsJson.GetRawText(), JsonOptions);
        if (settings == null)
        {
            return ConnectionTestResult.CreateFailure("Invalid LlmSettings");
        }

        var timeout = timeoutSeconds.HasValue ? (TimeSpan?)TimeSpan.FromSeconds(timeoutSeconds.Value) : null;
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
}

// Request DTO
public record TestConnectionRequest(
    string Category,
    JsonElement Settings,
    int? TimeoutSeconds = null);
