using System.Text.RegularExpressions;
using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Identity.Services;

namespace Connapse.Application.Agents;

/// <summary>Why an agent operation didn't succeed.</summary>
public enum AgentStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "agent-not-found", "Agent not found")]
    NotFound,

    [Failure(404, "agent-key-not-found", "Agent key not found")]
    KeyNotFound,

    [Failure(400, "invalid-agent", "Invalid agent")]
    InvalidAgent,

    [Failure(400, "invalid-agent-key", "Invalid agent key")]
    InvalidKey,

    [Failure(409, "agent-exists", "An agent with that name already exists")]
    NameTaken,
}

/// <summary>An agent's key to revoke.</summary>
public sealed record AgentKeyTarget(Guid AgentId, Guid KeyId);

/// <summary>Whether to turn an agent on or off.</summary>
public sealed record SetAgentActiveCommand(Guid AgentId, bool IsActive);

/// <summary>A new key for an agent.</summary>
public sealed record CreateAgentKeyCommand(Guid AgentId, CreateAgentKeyRequest Request);

internal static class AgentRules
{
    public const string NoPermission = "You don't have permission to manage agents.";

    public const string NotFound = "Agent not found";

    /// <summary>
    /// Agents are identities that act on Connapse, so managing them is user management: an administrator,
    /// holding the admin:users scope. A token of theirs issued for less can't mint an agent a key.
    /// </summary>
    public static async Task<bool> MayManageAsync(IOperationAuthorizer authorizer, OperationContext context) =>
        await authorizer.AllowsAsync(context.User, Policies.Admin) && await authorizer.AllowsAsync(context.User, Policies.AdminUsersScope);
}

/// <summary>Lists the agents that haven't been deleted, by name, each with its keys. Administrators only.</summary>
[Operation("agents.list", Scope = "admin:users", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListAgents(IAgentService agents, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<AgentDto>, AgentStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<IReadOnlyList<AgentDto>, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        return Attempt<IReadOnlyList<AgentDto>, AgentStatus>.Succeed(await agents.ListAsync(ct));
    }
}

/// <summary>Reads an agent and its keys. Administrators only.</summary>
[Operation("agents.get", Scope = "admin:users", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetAgent(IAgentService agents, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AgentDto, AgentStatus>> RunAsync(OperationContext context, Guid agentId, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        return await agents.GetAsync(agentId, ct) is { } agent
            ? Attempt<AgentDto, AgentStatus>.Succeed(agent)
            : Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.NotFound, AgentRules.NotFound);
    }
}

/// <summary>
/// Creates an agent, turned on and with no keys. Administrators only. The name is 2 to 64 letters,
/// digits, hyphens and underscores, unique among live agents; the description is at most 500 characters.
/// Both are stored trimmed, and a blank description is none.
/// </summary>
[Operation("agents.create", Scope = "admin:users", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class CreateAgent(IAgentService agents, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string CreatedEvent = "agent.created";

    public async Task<Attempt<AgentDto, AgentStatus>> RunAsync(OperationContext context, CreateAgentRequest request, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context) || UserRules.CallerId(context.User) is not Guid callerId)
            return Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);

        string name = request.Name?.Trim() ?? "";
        if (name.Length < ValidationConstants.MinAgentNameLength || name.Length > ValidationConstants.MaxAgentNameLength
            || !Regex.IsMatch(name, ValidationConstants.AgentNamePattern))
            return Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.InvalidAgent,
                $"Agent name must be {ValidationConstants.MinAgentNameLength}-{ValidationConstants.MaxAgentNameLength} characters, alphanumeric with hyphens and underscores.", "name");

        string? description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > ValidationConstants.MaxAgentDescriptionLength)
            return Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.InvalidAgent,
                $"Agent description must not exceed {ValidationConstants.MaxAgentDescriptionLength} characters.", "description");

        if (await agents.CreateAsync(new CreateAgentRequest(name, description), callerId, ct) is not { } agent)
            return Attempt<AgentDto, AgentStatus>.Fail(AgentStatus.NameTaken, $"An agent named '{name}' already exists.", "name");

        await audit.LogAsync(CreatedEvent, "agent", agent.Id.ToString(), new { agent.Name }, ct);
        return Attempt<AgentDto, AgentStatus>.Succeed(agent);
    }
}

/// <summary>Turns an agent on or off; an agent that's off can't use its keys. Administrators only.</summary>
[Operation("agents.setActive", Scope = "admin:users", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class SetAgentActive(IAgentService agents, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string EnabledEvent = "agent.enabled";

    public const string DisabledEvent = "agent.disabled";

    public async Task<Attempt<Guid, AgentStatus>> RunAsync(OperationContext context, SetAgentActiveCommand command, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        if (!await agents.SetActiveAsync(command.AgentId, command.IsActive, ct))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.NotFound, AgentRules.NotFound);

        await audit.LogAsync(command.IsActive ? EnabledEvent : DisabledEvent, "agent", command.AgentId.ToString(), null, ct);
        return Attempt<Guid, AgentStatus>.Succeed(command.AgentId);
    }
}

/// <summary>Deletes an agent and revokes every key it holds. Administrators only.</summary>
[Operation("agents.delete", Scope = "admin:users", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteAgent(IAgentService agents, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string DeletedEvent = "agent.deleted";

    public async Task<Attempt<Guid, AgentStatus>> RunAsync(OperationContext context, Guid agentId, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        if (!await agents.DeleteAsync(agentId, ct))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.NotFound, AgentRules.NotFound);

        await audit.LogAsync(DeletedEvent, "agent", agentId.ToString(), null, ct);
        return Attempt<Guid, AgentStatus>.Succeed(agentId);
    }
}

/// <summary>Lists an agent's keys, revoked ones included. A key's token is never listed. Administrators only.</summary>
[Operation("agents.keys.list", Scope = "admin:users", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListAgentKeys(IAgentService agents, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<AgentKeyListItem>, AgentStatus>> RunAsync(OperationContext context, Guid agentId, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<IReadOnlyList<AgentKeyListItem>, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        // The agent carries its keys, so an agent that doesn't exist is told apart from one with none.
        return await agents.GetAsync(agentId, ct) is { } agent
            ? Attempt<IReadOnlyList<AgentKeyListItem>, AgentStatus>.Succeed(agent.Keys)
            : Attempt<IReadOnlyList<AgentKeyListItem>, AgentStatus>.Fail(AgentStatus.NotFound, AgentRules.NotFound);
    }
}

/// <summary>
/// Gives an agent a key. Administrators only. The name is 2 to 64 characters, stored trimmed; the scopes
/// are ones an agent key can hold (<see cref="TokenScopeChoices.AgentKey"/>), and a key with none can call
/// no tool. The token is answered once, in the result, and never stored or audited.
/// </summary>
[Operation("agents.keys.create", Scope = "admin:users", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class CreateAgentKey(IAgentService agents, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string CreatedEvent = "agent.key.created";

    public async Task<Attempt<CreateAgentKeyResponse, AgentStatus>> RunAsync(OperationContext context, CreateAgentKeyCommand command, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<CreateAgentKeyResponse, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);

        var request = command.Request;
        string name = request.Name?.Trim() ?? "";
        if (name.Length < ValidationConstants.MinAgentNameLength || name.Length > ValidationConstants.MaxAgentKeyNameLength)
            return Attempt<CreateAgentKeyResponse, AgentStatus>.Fail(AgentStatus.InvalidKey,
                $"Agent key name must be {ValidationConstants.MinAgentNameLength}-{ValidationConstants.MaxAgentKeyNameLength} characters.", "name");

        string?[] scopes = (request.Scopes ?? []).Distinct(StringComparer.Ordinal).ToArray();
        // By index, not FirstOrDefault: a null scope would read as "none found" and let the ones after it through.
        int unknown = Array.FindIndex(scopes, s => s is null || !TokenScopeChoices.AgentKey.Contains(s, StringComparer.Ordinal));
        if (unknown >= 0)
            return Attempt<CreateAgentKeyResponse, AgentStatus>.Fail(AgentStatus.InvalidKey,
                $"'{scopes[unknown]}' is an invalid agent key scope. Scopes: {string.Join(", ", TokenScopeChoices.AgentKey)}.", "scopes");

        if (await agents.CreateKeyAsync(command.AgentId, request with { Name = name, Scopes = scopes! }, ct) is not { } key)
            return Attempt<CreateAgentKeyResponse, AgentStatus>.Fail(AgentStatus.NotFound, AgentRules.NotFound);

        // The key's name and id, never its token: an audit row is read by every administrator.
        await audit.LogAsync(CreatedEvent, "agent", command.AgentId.ToString(), new { Name = name, key.KeyId }, ct);
        return Attempt<CreateAgentKeyResponse, AgentStatus>.Succeed(key);
    }
}

/// <summary>Revokes one of an agent's keys, so it stops working. Administrators only.</summary>
[Operation("agents.keys.revoke", Scope = "admin:users", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class RevokeAgentKey(IAgentService agents, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string RevokedEvent = "agent.key.revoked";

    public async Task<Attempt<Guid, AgentStatus>> RunAsync(OperationContext context, AgentKeyTarget target, CancellationToken ct = default)
    {
        if (!await AgentRules.MayManageAsync(authorizer, context))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.Forbidden, AgentRules.NoPermission);
        if (!await agents.RevokeKeyAsync(target.AgentId, target.KeyId, ct))
            return Attempt<Guid, AgentStatus>.Fail(AgentStatus.KeyNotFound, "Key not found");

        await audit.LogAsync(RevokedEvent, "agent", target.AgentId.ToString(), new { target.KeyId }, ct);
        return Attempt<Guid, AgentStatus>.Succeed(target.KeyId);
    }
}

/// <summary>Every agent operation in one place, for the Agents page.</summary>
public sealed class AgentOperations(
    ListAgents list,
    GetAgent get,
    CreateAgent create,
    SetAgentActive setActive,
    DeleteAgent delete,
    ListAgentKeys listKeys,
    CreateAgentKey createKey,
    RevokeAgentKey revokeKey)
{
    public ListAgents List { get; } = list;

    public GetAgent Get { get; } = get;

    public CreateAgent Create { get; } = create;

    public SetAgentActive SetActive { get; } = setActive;

    public DeleteAgent Delete { get; } = delete;

    public ListAgentKeys ListKeys { get; } = listKeys;

    public CreateAgentKey CreateKey { get; } = createKey;

    public RevokeAgentKey RevokeKey { get; } = revokeKey;
}
