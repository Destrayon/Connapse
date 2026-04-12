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

public record AwsDeviceAuthStartResult(
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    string DeviceCode,
    int ExpiresInSeconds,
    int IntervalSeconds);
