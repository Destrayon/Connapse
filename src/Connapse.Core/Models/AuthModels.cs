namespace Connapse.Core;

public record LoginRequest(string Email, string Password);

public record RegisterRequest(string Email, string Password, string? DisplayName = null);

public record TokenResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    string TokenType = "Bearer");

public record RefreshTokenRequest(string RefreshToken);

public record PatCreateRequest(string Name, string[]? Scopes = null, DateTime? ExpiresAt = null);

public record PatCreateResponse(
    Guid Id,
    string Name,
    string Token,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt);

public record PatListItem(
    Guid Id,
    string Name,
    string TokenPrefix,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    bool IsRevoked);

public record UserListItem(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public record MeResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    DateTime CreatedAt);

public record AssignRolesRequest(IReadOnlyList<string> Roles);

public record CreateAgentRequest(string Name, string? Description = null);

public record CreateAgentKeyRequest(string Name, string[]? Scopes = null, DateTime? ExpiresAt = null);

public record SetAgentActiveRequest(bool IsActive);

public record AgentKeyListItem(
    Guid Id,
    string Name,
    string TokenPrefix,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? LastUsedAt,
    bool IsRevoked);

public record AgentDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    IReadOnlyList<AgentKeyListItem> Keys);

public record CreateAgentKeyResponse(
    Guid KeyId,
    string AgentId,
    string Token,
    string[] Scopes,
    DateTime CreatedAt,
    DateTime? ExpiresAt);

public record CloudIdentityDto(
    Guid Id,
    CloudProvider Provider,
    CloudIdentityData Data,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public record CloudIdentityData(
    string? PrincipalArn,
    string? AccountId,
    string? ObjectId,
    string? TenantId,
    string? DisplayName);

public record AzureConnectResult(string AuthorizeUrl, string State, string CodeVerifier);

/// <summary>
/// A user's connected IAM Identity Center identity, as the integrations page needs to show it.
/// </summary>
/// <remarks>
/// The directory user id is deliberately absent. It is the join key Connapse resolves permissions
/// with, and it means nothing to the person reading the page — who recognises their user name.
/// </remarks>
public record AwsIdentityLinkDto(
    string DirectoryUserName, string Email, DateTime ConnectedAt, DateTime? LastUsedAt);

/// <summary>
/// The outcome of disconnecting an AWS identity link.
/// </summary>
/// <remarks>
/// There is nothing to revoke at AWS: the link holds an identity rather than a credential, so
/// disconnecting is local and complete by definition. What remains worth reporting is
/// <see cref="LinkChangedDuringDisconnect"/>, which is a distinct outcome from a plain "nothing to
/// delete": a link existed but a reconnect replaced the row before the delete could run, so the row
/// was left in place rather than throwing away the link the user had just re-established.
/// <see cref="Deleted"/> is false in that case too, and the caller must tell the two apart to say
/// "try again" instead of "nothing was connected".
/// </remarks>
public record AwsIdentityLinkDisconnectResult(
    bool Deleted, bool LinkChangedDuringDisconnect = false);
