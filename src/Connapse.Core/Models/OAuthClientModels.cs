namespace Connapse.Core;

/// <summary>
/// An OAuth client with access to Connapse: one that registered itself, or one known by its metadata URL
/// (<paramref name="Registered"/> false) that holds grants. <paramref name="ActiveGrants"/> counts refresh
/// tokens neither revoked nor expired; <paramref name="Users"/> how many people granted them.
/// </summary>
public sealed record OAuthClientSummary(
    string ClientId,
    string Name,
    string ApplicationType,
    IReadOnlyList<string> RedirectUris,
    bool Registered,
    DateTime? RegisteredAt,
    string? RegisteredFromIp,
    int ActiveGrants,
    int Users,
    DateTime? LastGrantAt);

/// <summary>What revoking a client removed. Access tokens it already holds expire on their own, within their short lifetime.</summary>
public sealed record OAuthClientRevocation(string ClientId, bool ClientRemoved, int RefreshTokensRevoked, int AuthCodesRemoved);
