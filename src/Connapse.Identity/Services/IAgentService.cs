using Connapse.Core;

namespace Connapse.Identity.Services;

public interface IAgentService
{
    Task<AgentDto> CreateAsync(
        CreateAgentRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDto>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<AgentDto?> GetAsync(
        Guid agentId,
        CancellationToken cancellationToken = default);

    Task<CreateAgentKeyResponse> CreateKeyAsync(
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
