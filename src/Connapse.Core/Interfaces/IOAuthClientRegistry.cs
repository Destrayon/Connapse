using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>The OAuth clients that have access to Connapse, for administrators.</summary>
public interface IOAuthClientRegistry
{
    /// <summary>Registered clients, then clients known only by their metadata URL that still hold active grants.</summary>
    Task<IReadOnlyList<OAuthClientSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a client's registration, revokes its refresh tokens and deletes its pending authorization codes.
    /// Null when the client has none of them.
    /// </summary>
    Task<OAuthClientRevocation?> RevokeAsync(string clientId, CancellationToken ct = default);
}
