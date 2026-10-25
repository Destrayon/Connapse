using System.Text.Json;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Vectors;

namespace Connapse.Application.Settings;

/// <summary>An embedding model that has vectors: how many, of what size, and whether it is the configured one.</summary>
public record EmbeddingModelUsage(string ModelId, int Dimensions, long VectorCount, bool IsCurrent)
{
    public static IReadOnlyList<EmbeddingModelUsage> From(IEnumerable<EmbeddingModelInfo> models, string currentModel) =>
        models.Select(m => new EmbeddingModelUsage(
            m.ModelId, m.Dimensions, m.VectorCount,
            string.Equals(m.ModelId, currentModel, StringComparison.OrdinalIgnoreCase))).ToList();
}

/// <summary>The embedding models with vectors in any container, and whether any but the configured one has some.</summary>
public sealed record EmbeddingModels(string CurrentModel, IReadOnlyList<EmbeddingModelUsage> Models, bool HasLegacyVectors)
{
    /// <summary>How many vectors were made by models other than the configured one.</summary>
    public long LegacyVectorCount => Models.Where(m => !m.IsCurrent).Sum(m => m.VectorCount);
}

/// <summary>
/// What to re-embed. <see cref="Force"/> re-embeds every document; otherwise only those whose embedding
/// or chunking settings changed since they were indexed. <see cref="ContainerId"/> limits it to one container.
/// </summary>
public sealed record ReindexRequest(string? ContainerId = null, bool Force = false);

/// <summary>The latest re-index run and how many documents wait in the ingestion queue.</summary>
public sealed record ReindexProgress(int QueueDepth, ReindexState Run);

/// <summary>Summaries cleared across every container: how many containers, and how many document summaries.</summary>
public sealed record SummariesCleared(int Containers, int Documents);

/// <summary>
/// A connection test. <see cref="Category"/> is embedding, llm, crossencoder or minio; <see cref="Settings"/>
/// is that category's settings as JSON, keys included, since stored keys are never filled in.
/// </summary>
public sealed record ConnectionTestRequest(string? Category, JsonElement Settings, int? TimeoutSeconds = null);

/// <summary>
/// Lists the embedding models with vectors in any container. The configured model is the stored embedding
/// settings', or configuration's when nothing is stored, so it is right the moment a change is saved.
/// </summary>
[Operation("settings.embeddingModels", Scope = SettingsScope.Name, Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListEmbeddingModels(ISettingsStore store, IConfiguredSettings configured, VectorModelDiscovery models, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<EmbeddingModels, SettingsStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<EmbeddingModels, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can list embedding models.");

        var embedding = await store.GetAsync<EmbeddingSettings>(SettingsCategories.Embedding, ct) ?? configured.Current<EmbeddingSettings>();
        string current = EmbeddingIdentity.For(embedding);
        var usage = EmbeddingModelUsage.From(await models.GetModelsAsync(containerId: null, ct), current);
        return Attempt<EmbeddingModels, SettingsStatus>.Succeed(new EmbeddingModels(current, usage, usage.Any(m => !m.IsCurrent)));
    }
}

/// <summary>
/// Starts re-embedding in the background, and turns on cross-model search so documents still embedded with
/// a previous model stay findable until their turn comes.
/// </summary>
[Operation("settings.reindex", Scope = SettingsScope.Name, Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class StartReindex(
    ISettingsStore store, IConfiguredSettings configured, IBackgroundReindex reindex, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public const string StartedEvent = "settings.reindex_started";

    public async Task<Attempt<ReindexRequest, SettingsStatus>> RunAsync(OperationContext context, ReindexRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<ReindexRequest, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can re-index.");
        if (request.ContainerId is not null && !Guid.TryParse(request.ContainerId, out _))
            return Attempt<ReindexRequest, SettingsStatus>.Fail(SettingsStatus.Invalid, "containerId must be a container's id.", "containerId");

        var search = await store.GetAsync<SearchSettings>("search", ct) ?? configured.Current<SearchSettings>();
        if (!search.EnableCrossModelSearch)
            // A copy, so the configured value other readers share is never changed.
            await store.UpdateAsync<SearchSettings>("search",
                stored => (stored ?? configured.Current<SearchSettings>()) with { EnableCrossModelSearch = true }, ct);

        reindex.Start(new ReindexOptions { ContainerId = request.ContainerId, Force = request.Force, DetectSettingsChanges = true });
        await audit.LogAsync(StartedEvent, "settings", "reindex", new { request.ContainerId, request.Force }, ct);
        return Attempt<ReindexRequest, SettingsStatus>.Succeed(request);
    }
}

/// <summary>The latest re-index run, and the ingestion queue's depth as a measure of how far it has to go.</summary>
[Operation("settings.reindexStatus", Scope = SettingsScope.Name, Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetReindexStatus(ReindexStateService runs, IIngestionQueue queue, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ReindexProgress, SettingsStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<ReindexProgress, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can read re-index status.");

        return Attempt<ReindexProgress, SettingsStatus>.Succeed(new ReindexProgress(await queue.GetQueueDepthAsync(ct), runs.Current));
    }
}

/// <summary>
/// Clears every container's summary and every document summary, as clearing one container does for each;
/// the next sweep writes them again. Embeddings, chunks and documents are kept.
/// </summary>
[Operation("settings.summaries.clear", Scope = SettingsScope.Name, Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class ClearAllSummaries(IContainerStore containers, IDocumentStore documents, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public const string ClearedEvent = "settings.summaries_cleared";

    private const int PageSize = 200;

    public async Task<Attempt<SummariesCleared, SettingsStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<SummariesCleared, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can clear all summaries.");

        int containersCleared = 0;
        int documentsCleared = 0;
        for (int skip = 0; ; skip += PageSize)
        {
            var page = await containers.ListAsync(skip, PageSize, ct);
            foreach (var container in page)
            {
                if (!Guid.TryParse(container.Id, out var id))
                    continue;
                documentsCleared += await documents.ClearDocumentSummariesAsync(id, ct);
                await containers.UpdateSummaryAsync(id, null, null, null, ct);
                containersCleared++;
            }
            if (page.Count < PageSize)
                break;
        }

        var cleared = new SummariesCleared(containersCleared, documentsCleared);
        await audit.LogAsync(ClearedEvent, "settings", "summaries", cleared, ct);
        return Attempt<SummariesCleared, SettingsStatus>.Succeed(cleared);
    }
}

/// <summary>
/// Tests the connection the settings given describe, with the tester for their category and provider.
/// Nothing is stored, and a key left out is not filled in from the stored one: the settings can name any
/// address, and a stored key must never be sent to one the caller chose.
/// </summary>
[Operation("settings.testConnection", Scope = SettingsScope.Name, Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class TestConnection(IConnectionTests tests, ISettingsStore store, IConfiguredSettings configured, IOperationAuthorizer authorizer)
{
    /// <summary>The longest a test may be given, in seconds.</summary>
    public const int MaxTimeoutSeconds = 300;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<Attempt<ConnectionTestResult, SettingsStatus>> RunAsync(OperationContext context, ConnectionTestRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<ConnectionTestResult, SettingsStatus>.Fail(SettingsStatus.Forbidden, "Only administrators can test connections.");
        if (string.IsNullOrWhiteSpace(request.Category))
            return Attempt<ConnectionTestResult, SettingsStatus>.Fail(SettingsStatus.Invalid, "Category is required", "category");
        if (request.Settings.ValueKind is not JsonValueKind.Object)
            return Attempt<ConnectionTestResult, SettingsStatus>.Fail(SettingsStatus.Invalid, "Settings are required", "settings");
        if (request.TimeoutSeconds is < 1 or > MaxTimeoutSeconds)
            return Attempt<ConnectionTestResult, SettingsStatus>.Fail(SettingsStatus.Invalid,
                $"timeoutSeconds must be between 1 and {MaxTimeoutSeconds}.", "timeoutSeconds");

        (ConnectionTesterKind Kind, object Settings)? test;
        try
        {
            test = request.Category.ToLowerInvariant() switch
            {
                "embedding" => For(Parse<EmbeddingSettings>(request.Settings)),
                "llm" => For(Parse<LlmSettings>(request.Settings)),
                "crossencoder" => For(Parse<SearchSettings>(request.Settings)),
                "minio" => (ConnectionTesterKind.Minio, Parse<MinioOptions>(request.Settings)),
                // As the route has always answered: a failed test, not a refused request.
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            return Attempt<ConnectionTestResult, SettingsStatus>.Fail(SettingsStatus.Invalid, $"Invalid settings JSON: {ex.Message}", "settings");
        }
        if (test is not { } found)
            return Attempt<ConnectionTestResult, SettingsStatus>.Succeed(
                ConnectionTestResult.CreateFailure($"Category '{request.Category}' does not support connection testing"));

        var timeout = request.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        return Attempt<ConnectionTestResult, SettingsStatus>.Succeed(await tests.TestAsync(found.Kind, found.Settings, timeout, ct));
    }

    private static (ConnectionTesterKind, object) For(EmbeddingSettings settings) => (settings.Provider switch
    {
        "OpenAI" => ConnectionTesterKind.OpenAiEmbedding,
        "AzureOpenAI" => ConnectionTesterKind.AzureOpenAiEmbedding,
        _ => ConnectionTesterKind.Ollama,
    }, settings);

    private static (ConnectionTesterKind, object) For(LlmSettings settings) => (settings.Provider switch
    {
        "OpenAI" => ConnectionTesterKind.OpenAiLlm,
        "AzureOpenAI" => ConnectionTesterKind.AzureOpenAiLlm,
        "Anthropic" => ConnectionTesterKind.Anthropic,
        _ => ConnectionTesterKind.Ollama,
    }, settings);

    /// <summary>The cross-encoder's tester: the search settings name the reranker.</summary>
    private static (ConnectionTesterKind, object) For(SearchSettings settings) => (settings.CrossEncoderProvider switch
    {
        "Cohere" => ConnectionTesterKind.Cohere,
        "Jina" => ConnectionTesterKind.Jina,
        "AzureAIFoundry" => ConnectionTesterKind.AzureAIFoundry,
        "Voyage" => ConnectionTesterKind.Voyage,
        _ => ConnectionTesterKind.Tei,
    }, settings);

    /// <summary>The settings as <typeparamref name="T"/>; never null, as they are a JSON object.</summary>
    private static T Parse<T>(JsonElement settings) where T : class => JsonSerializer.Deserialize<T>(settings.GetRawText(), Json)!;
}
