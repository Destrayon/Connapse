using Connapse.Application.Connections;
using Connapse.Core;

namespace Connapse.Application.Ports;

/// <summary>
/// Asks a connection's provider what it can reach, as the Connections page's pickers do. A stored credential is only
/// used with the stored settings it was saved for; nothing is written.
/// </summary>
public interface IConnectionDiscovery
{
    /// <summary>What a stored connection reaches; Invalid for a provider with nothing to list.</summary>
    Task<Attempt<ConnectionDiscovery, ConnectionStatus>> DiscoverAsync(
        Connection connection, ConnectionDiscoveryRequest request, CancellationToken ct = default);

    /// <summary>What an unsaved connection of <see cref="DraftDiscovery.Provider"/> could reach.</summary>
    Task<Attempt<ConnectionDiscovery, ConnectionStatus>> DiscoverDraftAsync(DraftDiscovery draft, CancellationToken ct = default);
}
