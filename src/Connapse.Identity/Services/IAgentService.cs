using Connapse.Core;

namespace Connapse.Identity.Services;

public interface IAgentService
{
    /// <summary>Creates an agent; null when a live agent already has that name.</summary>
    Task<AgentDto?> CreateAsync(
        CreateAgentRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDto>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AgentDto?> GetAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a key for a live agent; null when there's no such agent.</summary>
    Task<CreateAgentKeyResponse?> CreateKeyAsync(
        Guid agentId,
        CreateAgentKeyRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeKeyAsync(
        Guid agentId,
        Guid keyId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentKeyListItem>> ListKeysAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    Task<bool> SetActiveAsync(
        Guid agentId,
        bool isActive,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);
}
