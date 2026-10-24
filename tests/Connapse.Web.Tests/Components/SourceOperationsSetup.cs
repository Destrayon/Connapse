using System.Security.Claims;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Sources operations for a page test, over the test's own substituted stores. These tests are
/// about what a page shows, so every policy check passes; whether a caller may act is the
/// cross-surface harness's question, asked against the real policies.
/// </summary>
public static class SourceOperationsSetup
{
    public static IServiceCollection AddSourceOperations(this IServiceCollection services)
    {
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        services.AddSingleton(authorizer);
        services.AddScoped<ISourceVisibility>(sp => sp.GetService<PrivateSourceVisibility>() ?? (ISourceVisibility)new SeesEverything());
        services.AddScoped<ISourceScopeRules, SourceScopeRules>();
        // Pages that only edit (SourceEdit) register none of what creating checks with; they never create.
        services.AddScoped(sp => sp.GetService<CustomSourceSetup>() is null
            ? Substitute.For<ISourceCreateRules>()
            : ActivatorUtilities.CreateInstance<SourceCreateRules>(sp));
        // Pages that register the sync service sync through it; the rest never sync.
        services.AddScoped(sp => sp.GetService<SourceSyncService>() as ISourceSync ?? Substitute.For<ISourceSync>());
        // A page reads its caller from here. bUnit's own placeholder throws until a test signs someone
        // in, so a test that didn't gets a signed-in caller; one that did keeps its own.
        if (services.LastOrDefault(d => d.ServiceType == typeof(AuthenticationStateProvider)) is not { } current
            || (current.ImplementationType ?? current.ImplementationInstance?.GetType())?.Name.Contains("Placeholder", StringComparison.Ordinal) == true)
            services.Replace(ServiceDescriptor.Singleton<AuthenticationStateProvider>(new SignedIn()));
        return services.AddApplication();
    }

    private sealed class SeesEverything : ISourceVisibility
    {
        public Task<Func<Connapse.Core.Source, bool>> ForAsync(ClaimsPrincipal? caller, CancellationToken ct = default) =>
            Task.FromResult<Func<Connapse.Core.Source, bool>>(_ => true);
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
    }
}
