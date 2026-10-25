using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Containers;

/// <summary>A container's settings overrides; empty when it has none, so every setting is the global one.</summary>
[Operation("containers.settings.get", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetContainerSettings(IContainerStore containers, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ContainerSettingsOverrides, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<ContainerSettingsOverrides, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to view containers.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<ContainerSettingsOverrides>(ContainerKey.ById(containerId));

        return Attempt<ContainerSettingsOverrides, ContainerStatus>.Succeed(
            await containers.GetSettingsOverridesAsync(containerId, ct) ?? new ContainerSettingsOverrides());
    }
}

/// <summary>Replaces a container's settings overrides. Editors and above; a null section means the global setting.</summary>
[Operation("containers.settings.update", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class UpdateContainerSettings(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ContainerSettingsOverrides, ContainerStatus>> RunAsync(
        OperationContext context, Guid containerId, ContainerSettingsOverrides overrides, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Attempt<ContainerSettingsOverrides, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to change containers.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<ContainerSettingsOverrides>(ContainerKey.ById(containerId));

        await containers.SaveSettingsOverridesAsync(containerId, overrides, ct);
        await audit.LogAsync("container.settings_updated", "container", containerId.ToString(), cancellationToken: ct);
        return Attempt<ContainerSettingsOverrides, ContainerStatus>.Succeed(overrides);
    }
}

/// <summary>Removes every override, so the container follows the global settings again. Editors and above.</summary>
[Operation("containers.settings.reset", Scope = "knowledge:write", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ResetContainerSettings(IContainerStore containers, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ContainerSettingsOverrides, ContainerStatus>> RunAsync(OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Writer))
            return Attempt<ContainerSettingsOverrides, ContainerStatus>.Fail(ContainerStatus.Forbidden, "You don't have permission to change containers.");
        if (!await containers.ExistsAsync(containerId, ct))
            return ContainerRules.NotFound<ContainerSettingsOverrides>(ContainerKey.ById(containerId));

        var cleared = new ContainerSettingsOverrides();
        await containers.SaveSettingsOverridesAsync(containerId, cleared, ct);
        await audit.LogAsync("container.settings_reset", "container", containerId.ToString(), cancellationToken: ct);
        return Attempt<ContainerSettingsOverrides, ContainerStatus>.Succeed(cleared);
    }
}
