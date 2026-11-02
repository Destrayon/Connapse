using Connapse.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Architecture.Tests.Canaries;

// Deliberate violations. The canary tests run the production rules over these types and require
// every one to be reported, so a rule that goes blind fails here instead of passing silently.
// The store and locator sit inside async lambdas because that is where ArchUnitNET goes blind.

internal sealed class PlantedComponent : ComponentBase
{
    [Inject] public ISourceStore Sources { get; set; } = null!;
    [Inject] public IServiceScopeFactory Scopes { get; set; } = null!;
    [Inject] public Lazy<IFolderStore> Folders { get; set; } = null!;
}

internal sealed class PlantedConstructorComponent(ISettingsStore settings) : ComponentBase
{
    public ISettingsStore Settings => settings;
}

internal static class PlantedEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/planted", async (ISourceStore sources, IServiceProvider services, CancellationToken ct) =>
        {
            await Task.Yield();
            return Results.Ok(await sources.ListAsync(ct: ct));
        });
    }
}

// Operations with incomplete metadata (#831). A complete one sits beside them, so the rule must
// report exactly the broken ones.
[Connapse.Application.Operation("planted.complete", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal sealed class PlantedCompleteOperation;

[Connapse.Application.Operation("planted.noscope", Risk = Connapse.Application.RiskTier.Write)]
internal sealed class PlantedOperationWithoutScope;

[Connapse.Application.Operation("planted.norisk", Scope = "knowledge:write")]
internal sealed class PlantedOperationWithoutRisk;

[Connapse.Application.Operation("planted.undefinedrisk", Scope = "knowledge:write", Risk = (Connapse.Application.RiskTier)42)]
internal sealed class PlantedOperationWithUndefinedRisk;

[Connapse.Application.Operation(" ", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal sealed class PlantedOperationWithoutId;

[Connapse.Application.Operation("planted.abstract", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal abstract class PlantedAbstractOperation;

[Connapse.Application.Operation("planted.shared", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal sealed class PlantedSharedIdA;

[Connapse.Application.Operation("planted.shared", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal sealed class PlantedSharedIdB;

// An operation whose status enum REST can't fully map (#833). NotFound is the correct one.
public enum PlantedStatus
{
    Success,
    [Connapse.Application.Failure(404, "planted-not-found", "Planted not found")]
    NotFound,
    Unmapped,
    [Connapse.Application.Failure(200, "planted-ok", "Planted ok")]
    NotAFailure,
    [Connapse.Application.Failure(400, "planted-undocumented", "Planted undocumented")]
    Undocumented,
    [Connapse.Application.Failure(409, "planted-not-found", "Planted conflict")]
    Conflicting,
}

[Connapse.Application.Operation("planted.failures", Scope = "knowledge:read", Risk = Connapse.Application.RiskTier.Read)]
internal sealed class PlantedOperationWithFailures
{
    public Task<Connapse.Application.Attempt<string, PlantedStatus>> RunAsync() =>
        Task.FromResult(Connapse.Application.Attempt<string, PlantedStatus>.Fail(PlantedStatus.NotFound));
}

// MCP tools that reach past the operations (CON-19): a store, a locator, and a validator the
// operation already applies. The operation, plumbing and arguments beside them are allowed.
[ModelContextProtocol.Server.McpServerToolType]
internal static class PlantedMcpTools
{
    [ModelContextProtocol.Server.McpServerTool(Name = "planted_store_tool")]
    public static Task<string> StoreTool(ISourceStore sources, IServiceProvider services, string name, CancellationToken ct) =>
        Task.FromResult(name);

    [ModelContextProtocol.Server.McpServerTool(Name = "planted_rule_tool")]
    public static Task<string> RuleTool(PlantedCompleteOperation operation, IFileTypeValidator types,
        Microsoft.AspNetCore.Http.IHttpContextAccessor http, int? topK, Guid id, CancellationToken ct) =>
        Task.FromResult(id.ToString());
}
