namespace Connapse.Core.Interfaces;

public interface IContainerStore
{
    Task<Container> CreateAsync(CreateContainerRequest request, CancellationToken ct = default);
    Task<Container?> GetAsync(Guid id, CancellationToken ct = default);
    Task<Container?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Container>> ListAsync(int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
    Task<ContainerSettingsOverrides?> GetSettingsOverridesAsync(Guid id, CancellationToken ct = default);
    Task SaveSettingsOverridesAsync(Guid id, ContainerSettingsOverrides overrides, CancellationToken ct = default);
    Task UpdateConnectorConfigAsync(Guid id, string? connectorConfig, CancellationToken ct = default);
}
