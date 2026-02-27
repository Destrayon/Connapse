using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Identity.Authentication;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class AgentService(
    ConnapseIdentityDbContext dbContext,
    ILogger<AgentService> logger) : IAgentService
{
    public async Task<AgentDto> CreateAsync(
        CreateAgentRequest request,
        Guid createdByUserId,
        CancellationToken cancellationToken = default)
    {
        var entity = new AgentEntity
        {
            Name = request.Name,
            Description = request.Description,
            IsActive = true,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.Agents.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created agent '{Name}' (Id: {Id}) by user {UserId}",
            LogSanitizer.Sanitize(entity.Name), entity.Id, createdByUserId);

        return MapToDto(entity);
    }

    public async Task<IReadOnlyList<AgentDto>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var agents = await dbContext.Agents
            .Include(a => a.ApiKeys)
            .Where(a => a.DeletedAt == null)
            .OrderBy(a => a.Name)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return agents.Select(MapToDto).ToList();
    }

    public async Task<AgentDto?> GetAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var agent = await dbContext.Agents
            .Include(a => a.ApiKeys)
            .Where(a => a.Id == agentId && a.DeletedAt == null)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        return agent is null ? null : MapToDto(agent);
    }

    public async Task<CreateAgentKeyResponse> CreateKeyAsync(
        Guid agentId,
        CreateAgentKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await dbContext.Agents
            .FirstOrDefaultAsync(a => a.Id == agentId && a.DeletedAt == null, cancellationToken)
            ?? throw new InvalidOperationException($"Agent {agentId} not found.");

        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var token = ApiKeyAuthenticationOptions.TokenPrefix + Base64UrlEncode(randomBytes);
        var tokenHash = ComputeSha256Hash(token);
        var tokenPrefix = token[..12];

        var keyEntity = new AgentApiKeyEntity
        {
            AgentId = agent.Id,
            Name = request.Name,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            Scopes = request.Scopes is { Length: > 0 } s ? string.Join(",", s) : "",
            ExpiresAt = request.ExpiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.AgentApiKeys.Add(keyEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created API key '{Name}' (prefix: {Prefix}) for agent {AgentId}",
            LogSanitizer.Sanitize(request.Name), tokenPrefix, agentId);

        return new CreateAgentKeyResponse(
            KeyId: keyEntity.Id,
            AgentId: agentId.ToString(),
            Token: token,
            Scopes: SplitScopes(keyEntity.Scopes),
            CreatedAt: keyEntity.CreatedAt,
            ExpiresAt: keyEntity.ExpiresAt);
    }

    public async Task<bool> RevokeKeyAsync(
        Guid agentId,
        Guid keyId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.AgentApiKeys
            .Where(k => k.Id == keyId && k.AgentId == agentId && k.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow),
                cancellationToken);

        if (rows > 0)
            logger.LogInformation("Revoked API key {KeyId} for agent {AgentId}", keyId, agentId);

        return rows > 0;
    }

    public async Task<bool> SetActiveAsync(
        Guid agentId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Agents
            .Where(a => a.Id == agentId && a.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.IsActive, isActive),
                cancellationToken);

        return rows > 0;
    }

    public async Task<bool> DeleteAsync(
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Agents
            .Where(a => a.Id == agentId && a.DeletedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(a => a.DeletedAt, DateTime.UtcNow)
                      .SetProperty(a => a.IsActive, false),
                cancellationToken);

        if (rows > 0)
        {
            await dbContext.AgentApiKeys
                .Where(k => k.AgentId == agentId && k.RevokedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow),
                    cancellationToken);

            logger.LogInformation("Soft-deleted agent {AgentId}", agentId);
        }

        return rows > 0;
    }

    private static AgentDto MapToDto(AgentEntity agent) => new(
        agent.Id,
        agent.Name,
        agent.Description,
        agent.IsActive,
        agent.CreatedByUserId,
        agent.CreatedAt,
        agent.ApiKeys.Select(k => new AgentKeyListItem(
            k.Id,
            k.Name,
            k.TokenPrefix,
            SplitScopes(k.Scopes),
            k.CreatedAt,
            k.ExpiresAt,
            k.LastUsedAt,
            k.RevokedAt != null)).ToList());

    private static string[] SplitScopes(string scopes) =>
        string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
