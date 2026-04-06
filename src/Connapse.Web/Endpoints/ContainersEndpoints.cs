using System.Security.Claims;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors;
using Connapse.Storage.Vectors;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Endpoints;

public static class ContainersEndpoints
{
    public static IEndpointRouteBuilder MapContainersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/containers").WithTags("Containers");

        // POST /api/containers - Create container
        group.MapPost("/", async (
            [FromBody] CreateContainerApiRequest request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] ConnectorWatcherService watcherService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Container name is required" });

            var trimmedName = request.Name.Trim();

            if (!PathUtilities.IsValidContainerName(trimmedName))
                return Results.BadRequest(new { error = "Container name must be 2-128 characters, lowercase alphanumeric and hyphens, cannot start or end with a hyphen" });

            var normalizedName = trimmedName;

            // Validate connector config before creating
            if (request.ConnectorType is ConnectorType.Filesystem or ConnectorType.S3 or ConnectorType.AzureBlob)
            {
                if (string.IsNullOrWhiteSpace(request.ConnectorConfig))
                {
                    var configHint = request.ConnectorType switch
                    {
                        ConnectorType.Filesystem => "rootPath",
                        ConnectorType.S3 => "bucketName and region",
                        ConnectorType.AzureBlob => "storageAccountName and containerName",
                        _ => "configuration"
                    };
                    return Results.BadRequest(new { error = $"{request.ConnectorType} connector requires {configHint} (provide connector config JSON)." });
                }

                try { JsonDocument.Parse(request.ConnectorConfig); }
                catch (JsonException ex)
                    { return Results.BadRequest(new { error = $"Invalid connector config JSON: {ex.Message}" }); }
            }

            var existing = await containerStore.GetByNameAsync(normalizedName, ct);
            if (existing is not null)
                return Results.Conflict(new { error = $"Container '{normalizedName}' already exists" });

            var container = await containerStore.CreateAsync(
                new CreateContainerRequest(normalizedName, request.Description, request.ConnectorType, request.ConnectorConfig), ct);

            await auditLogger.LogAsync("container.created", "container", container.Id.ToString(),
                new { container.Name, container.ConnectorType }, ct);

            // Start watching (Filesystem) or polling (S3/AzureBlob/MinIO).
            watcherService.StartWatchingContainer(container);

            return Results.Created($"/api/containers/{container.Id}", container);
        })
        .WithName("CreateContainer")
        .WithDescription("Create a new container for organizing files")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers - List containers (paginated)
        group.MapGet("/", async (
            [FromQuery] int? skip,
            [FromQuery] int? take,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var effectiveSkip = skip ?? 0;
            var effectiveTake = take ?? 50;
            var validationError = PaginationValidator.Validate(effectiveSkip, effectiveTake);
            if (validationError is not null) return validationError;

            var containers = await containerStore.ListAsync(effectiveSkip, effectiveTake + 1, ct);
            var hasMore = containers.Count > effectiveTake;
            var items = hasMore ? containers.Take(effectiveTake).ToList() : containers;

            return Results.Ok(new PagedResponse<Container>(items, items.Count, hasMore));
        })
        .WithName("ListContainers")
        .WithDescription("List containers with pagination (?skip=0&take=50)")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId} - Get container details
        group.MapGet("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            return container is not null
                ? Results.Ok(container)
                : Results.NotFound(new { error = $"Container {containerId} not found" });
        })
        .WithName("GetContainer")
        .WithDescription("Get a specific container by ID")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/by-name/{name} - Resolve container by name
        group.MapGet("/by-name/{name}", async (
            string name,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetByNameAsync(name.ToLowerInvariant(), ct);
            return container is not null
                ? Results.Ok(container)
                : Results.NotFound(new { error = $"Container '{name}' not found" });
        })
        .WithName("GetContainerByName")
        .WithDescription("Get a specific container by name")
        .RequireAuthorization("RequireViewer");

        // DELETE /api/containers/{containerId} - Delete container (must be empty for storage-backed connectors; Filesystem containers just stop being watched)
        group.MapDelete("/{containerId:guid}", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] ConnectorWatcherService watcherService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            try
            {
                var deleted = await containerStore.DeleteAsync(containerId, ct);
                if (!deleted)
                    return Results.NotFound(new { error = $"Container {containerId} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            await auditLogger.LogAsync("container.deleted", "container", containerId.ToString(),
                new { Name = container.Name }, ct);

            // Stop the watcher/poller if one was running for this container.
            watcherService.StopWatchingContainer(containerId);

            return Results.NoContent();
        })
        .WithName("DeleteContainer")
        .WithDescription("Delete a container. MinIO containers must be empty first. Filesystem, S3, and AzureBlob containers just stop being indexed — the underlying data is not deleted.")
        .RequireAuthorization("RequireEditor");

        // GET /api/containers/{containerId}/stats - Get container statistics
        group.MapGet("/{containerId:guid}/stats", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IDocumentStore documentStore,
            [FromServices] VectorModelDiscovery modelDiscovery,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var stats = await documentStore.GetContainerStatsAsync(containerId, ct);
            var models = await modelDiscovery.GetModelsAsync(containerId, ct);

            return Results.Ok(new
            {
                containerId = container.Id,
                containerName = container.Name,
                connectorType = container.ConnectorType.ToString(),
                documents = new
                {
                    total = stats.DocumentCount,
                    ready = stats.ReadyCount,
                    processing = stats.ProcessingCount,
                    failed = stats.FailedCount
                },
                totalChunks = stats.TotalChunks,
                totalSizeBytes = stats.TotalSizeBytes,
                embeddingModels = models.Select(m => new
                {
                    modelId = m.ModelId,
                    dimensions = m.Dimensions,
                    vectorCount = m.VectorCount
                }),
                lastIndexedAt = stats.LastIndexedAt,
                createdAt = container.CreatedAt
            });
        })
        .WithName("GetContainerStats")
        .WithDescription("Get statistics for a container: document counts by status, chunk count, storage size, embedding models, last indexed time")
        .RequireAuthorization("RequireViewer");

        // GET /api/containers/{containerId}/settings - Get container settings overrides
        group.MapGet("/{containerId:guid}/settings", async (
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var overrides = await containerStore.GetSettingsOverridesAsync(containerId, ct);
            return Results.Ok(overrides ?? new ContainerSettingsOverrides());
        })
        .WithName("GetContainerSettings")
        .WithDescription("Get per-container settings overrides")
        .RequireAuthorization("RequireViewer");

        // PUT /api/containers/{containerId}/settings - Save container settings overrides
        group.MapPut("/{containerId:guid}/settings", async (
            Guid containerId,
            [FromBody] ContainerSettingsOverrides overrides,
            [FromServices] IContainerStore containerStore,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            await containerStore.SaveSettingsOverridesAsync(containerId, overrides, ct);
            return Results.Ok(overrides);
        })
        .WithName("SaveContainerSettings")
        .WithDescription("Save per-container settings overrides")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/reindex - Reindex documents in container
        group.MapPost("/{containerId:guid}/reindex", async (
            Guid containerId,
            [FromBody] ContainerReindexRequest? request,
            [FromServices] IContainerStore containerStore,
            [FromServices] IReindexService reindexService,
            CancellationToken ct) =>
        {
            if (!await containerStore.ExistsAsync(containerId, ct))
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            var options = new ReindexOptions
            {
                ContainerId = containerId.ToString(),
                Force = request?.Force ?? false,
                DetectSettingsChanges = request?.DetectSettingsChanges ?? true,
                Strategy = request?.Strategy
            };

            var result = await reindexService.ReindexAsync(options, ct);

            return Results.Ok(new
            {
                batchId = result.BatchId,
                totalDocuments = result.TotalDocuments,
                enqueuedCount = result.EnqueuedCount,
                skippedCount = result.SkippedCount,
                failedCount = result.FailedCount,
                reasonCounts = result.ReasonCounts.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => kvp.Value),
                message = $"Reindex complete: {result.EnqueuedCount} enqueued, {result.SkippedCount} skipped, {result.FailedCount} failed"
            });
        })
        .WithName("ReindexContainer")
        .WithDescription("Reindex all documents in a container")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/test-connection - Test connector config before creating a container
        group.MapPost("/test-connection", async (
            [FromBody] TestConnectorConfigRequest request,
            [FromServices] S3ConnectionTester s3Tester,
            [FromServices] AzureBlobConnectionTester azureTester,
            [FromServices] MinioConnectionTester minioTester,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConnectorConfig))
                return Results.BadRequest(new { error = "ConnectorConfig is required" });

            try
            {
                var timeout = request.TimeoutSeconds.HasValue ? TimeSpan.FromSeconds(request.TimeoutSeconds.Value) : (TimeSpan?)null;
                var result = request.ConnectorType switch
                {
                    ConnectorType.S3 => await s3Tester.TestConnectionAsync(
                        request.ConnectorConfig, timeout, ct),
                    ConnectorType.AzureBlob => await azureTester.TestConnectionAsync(
                        request.ConnectorConfig, timeout, ct),
                    ConnectorType.ManagedStorage => await minioTester.TestConnectionAsync(
                        request.ConnectorConfig, timeout, ct),
                    _ => ConnectionTestResult.CreateFailure(
                        $"Connector type '{request.ConnectorType}' does not support connection testing from this endpoint")
                };
                return Results.Ok(result);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"Invalid connector config JSON: {ex.Message}" });
            }
        })
        .WithName("TestConnectorConfig")
        .WithDescription("Test connectivity for S3, AzureBlob, or MinIO connector config before creating a container")
        .RequireAuthorization("RequireEditor");

        // POST /api/containers/{containerId}/sync - Sync files from remote connector
        group.MapPost("/{containerId:guid}/sync", async (
            HttpContext httpContext,
            Guid containerId,
            [FromServices] IContainerStore containerStore,
            [FromServices] IConnectorFactory connectorFactory,
            [FromServices] IDocumentStore documentStore,
            [FromServices] IIngestionQueue queue,
            [FromServices] IOptionsMonitor<ChunkingSettings> chunkingSettings,
            [FromServices] IAuditLogger auditLogger,
            [FromServices] ICloudScopeService cloudScopeService,
            CancellationToken ct) =>
        {
            var container = await containerStore.GetAsync(containerId, ct);
            if (container is null)
                return Results.NotFound(new { error = $"Container {containerId} not found" });

            // Cloud scope enforcement — user must have a linked identity for the provider
            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(userId, out var uid))
            {
                var scopeResult = await cloudScopeService.GetScopesAsync(uid, container, ct);
                if (scopeResult is { HasAccess: false })
                    return Results.Json(new
                    {
                        error = "cloud_access_denied",
                        message = scopeResult.Error ?? "Access denied.",
                        containerId = containerId.ToString()
                    }, statusCode: 403);
            }

            if (container.ConnectorType == ConnectorType.Filesystem)
                return Results.BadRequest(new { error = "Filesystem containers use live watch. Sync is not needed." });

            var connector = connectorFactory.Create(container);
            var remoteFiles = await connector.ListFilesAsync(ct: ct);

            var existingDocs = await documentStore.ListAsync(containerId, take: int.MaxValue, ct: ct);
            var existingByPath = existingDocs.ToDictionary(d => d.Path);

            var batchId = Guid.NewGuid().ToString();
            int enqueued = 0, skipped = 0;

            var strategy = Enum.TryParse<ChunkingStrategy>(
                chunkingSettings.CurrentValue.Strategy, ignoreCase: true, out var parsed)
                ? parsed
                : ChunkingStrategy.Recursive;

            // Collect jobs to enqueue, pre-registering new documents in the DB first
            var jobsToEnqueue = new List<IngestionJob>();

            foreach (var file in remoteFiles)
            {
                var virtualPath = file.Path.StartsWith('/') ? file.Path : "/" + file.Path;

                if (existingByPath.TryGetValue(virtualPath, out var existing))
                {
                    var status = existing.Metadata.GetValueOrDefault("Status");
                    if (status is "Ready" or "Failed")
                    {
                        skipped++;
                        continue;
                    }
                }

                var fileName = Path.GetFileName(virtualPath);
                var isNew = !existingByPath.TryGetValue(virtualPath, out var doc);
                var documentId = isNew ? Guid.NewGuid().ToString() : doc!.Id;
                var contentType = GetContentType(fileName);

                // Pre-register new documents so they appear in the file browser immediately
                if (isNew)
                {
                    await documentStore.StoreAsync(new Document(
                        Id: documentId,
                        ContainerId: container.Id,
                        FileName: fileName,
                        ContentType: contentType,
                        Path: virtualPath,
                        SizeBytes: file.SizeBytes,
                        CreatedAt: DateTime.UtcNow,
                        Metadata: new Dictionary<string, string>
                        {
                            ["Source"] = "ConnectorSync",
                            ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                        }), ct);
                }

                jobsToEnqueue.Add(new IngestionJob(
                    JobId: Guid.NewGuid().ToString(),
                    DocumentId: documentId,
                    Path: virtualPath,
                    Options: new IngestionOptions(
                        DocumentId: documentId,
                        FileName: fileName,
                        ContentType: contentType,
                        ContainerId: container.Id,
                        Path: virtualPath,
                        Strategy: strategy,
                        Metadata: new Dictionary<string, string>
                        {
                            ["OriginalFileName"] = fileName,
                            ["Source"] = "ConnectorSync",
                            ["SyncedAt"] = DateTime.UtcNow.ToString("O")
                        }),
                    BatchId: batchId));

                enqueued++;
            }

            foreach (var job in jobsToEnqueue)
                await queue.EnqueueAsync(job, ct);

            await auditLogger.LogAsync("container.synced", "container", containerId.ToString(),
                new { enqueued, skipped, total = remoteFiles.Count }, ct);

            return Results.Ok(new
            {
                batchId,
                totalFiles = remoteFiles.Count,
                enqueuedCount = enqueued,
                skippedCount = skipped,
                message = $"Sync complete: {enqueued} enqueued, {skipped} skipped"
            });
        })
        .WithName("SyncContainer")
        .WithDescription("Sync files from a remote connector (S3, AzureBlob, MinIO). Lists remote files, compares to DB, enqueues new/changed.")
        .RequireAuthorization("RequireEditor");

        return app;
    }

    private static string? GetContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => null
        };
}

// Request DTOs for container endpoints
public record CreateContainerApiRequest(
    string Name,
    string? Description = null,
    ConnectorType ConnectorType = ConnectorType.ManagedStorage,
    string? ConnectorConfig = null);

public record ContainerReindexRequest(
    bool? Force = null,
    bool? DetectSettingsChanges = null,
    ChunkingStrategy? Strategy = null);

public record TestConnectorConfigRequest(
    ConnectorType ConnectorType,
    string ConnectorConfig,
    int? TimeoutSeconds = null);
