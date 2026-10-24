using Connapse.Core;
using Connapse.Storage.Connectors.Custom;

namespace Connapse.Application.Ports;

/// <summary>
/// Links to custom providers, which live on each provider's source server. Web answers it with the
/// service that talks to those servers, which Connapse.Application can't reference.
/// </summary>
public interface ICustomAccountLinks
{
    /// <summary>Providers a user can link to or unlink from.</summary>
    Task<IReadOnlyList<CustomProvider>> LinkableAsync(CancellationToken ct);

    /// <summary>The user's link, read live from the provider's source server. Throws when it can't say.</summary>
    Task<CustomLinkStatus> StatusAsync(Guid providerId, Guid userId, CancellationToken ct);
}
