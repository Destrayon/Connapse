using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnapseCore(this IServiceCollection services)
    {
        // Core registrations (shared services, options validation)
        return services;
    }
}
