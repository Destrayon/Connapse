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
