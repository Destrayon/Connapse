using Connapse.Application.Ports;
using Connapse.Core;

namespace Connapse.Application.Providers;

/// <summary>Why a provider action didn't succeed.</summary>
public enum ProviderStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,
}

/// <summary>
/// Each built-in provider (AWS, Azure, GitHub, Atlassian) with every requirement it still has, in the
/// order the providers page shows them. Reading it may call the provider: AWS, Entra and GitHub are
/// asked whether they still accept Connapse's credentials, so the answer is live, not remembered.
/// Settings administrators only, since the details name tenants, roles and Apps.
/// </summary>
[Operation("providers.status", Scope = "admin:settings", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetProviderStatus(IProviderSetupReader reader, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<ProviderSetup>, ProviderStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<IReadOnlyList<ProviderSetup>, ProviderStatus>.Fail(ProviderStatus.Forbidden, "You don't have permission to view provider setup.");
        return Attempt<IReadOnlyList<ProviderSetup>, ProviderStatus>.Succeed(await reader.ReadAsync(ct));
    }
}
