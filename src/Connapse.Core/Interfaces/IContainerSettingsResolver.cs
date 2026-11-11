namespace Connapse.Core.Interfaces;

public interface IContainerSettingsResolver
{
    Task<ChunkingSettings> GetChunkingSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<EmbeddingSettings> GetEmbeddingSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<SearchSettings> GetSearchSettingsAsync(Guid containerId, CancellationToken ct = default);
    Task<UploadSettings> GetUploadSettingsAsync(Guid containerId, CancellationToken ct = default);

    /// <summary>The container's table settings; they have no global value, so a container without them gets the defaults.</summary>
    Task<TableSettings> GetTableSettingsAsync(Guid containerId, CancellationToken ct = default);
}
