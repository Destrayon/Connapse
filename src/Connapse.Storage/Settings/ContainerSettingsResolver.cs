using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Settings;

/// <summary>
/// Resolves effective settings for a container by merging per-container overrides
/// with global DB settings. Resolution order (highest wins):
///   appsettings → global DB settings → container settings_overrides
/// </summary>
public class ContainerSettingsResolver(
    KnowledgeDbContext context,
    ISettingsStore settingsStore) : IContainerSettingsResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ChunkingSettings> GetChunkingSettingsAsync(Guid containerId, CancellationToken ct = default)
    {
        var overrides = await GetOverridesAsync(containerId, ct);
        return overrides?.Chunking
            ?? await settingsStore.GetAsync<ChunkingSettings>("Chunking", ct)
            ?? new ChunkingSettings();
    }

    public async Task<EmbeddingSettings> GetEmbeddingSettingsAsync(Guid containerId, CancellationToken ct = default)
    {
        var overrides = await GetOverridesAsync(containerId, ct);
        return overrides?.Embedding
            ?? await settingsStore.GetAsync<EmbeddingSettings>("Embedding", ct)
            ?? new EmbeddingSettings();
    }

    public async Task<SearchSettings> GetSearchSettingsAsync(Guid containerId, CancellationToken ct = default)
    {
        var overrides = await GetOverridesAsync(containerId, ct);
        return overrides?.Search
            ?? await settingsStore.GetAsync<SearchSettings>("Search", ct)
            ?? new SearchSettings();
    }

    public async Task<UploadSettings> GetUploadSettingsAsync(Guid containerId, CancellationToken ct = default)
    {
        var overrides = await GetOverridesAsync(containerId, ct);
        return overrides?.Upload
            ?? await settingsStore.GetAsync<UploadSettings>("Upload", ct)
            ?? new UploadSettings();
    }

    private async Task<ContainerSettingsOverrides?> GetOverridesAsync(Guid containerId, CancellationToken ct)
    {
        var json = await context.Containers
            .AsNoTracking()
            .Where(c => c.Id == containerId)
            .Select(c => c.SettingsOverridesJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(json))
            return null;

        try { return JsonSerializer.Deserialize<ContainerSettingsOverrides>(json, JsonOptions); }
        catch { return null; }
    }
}
