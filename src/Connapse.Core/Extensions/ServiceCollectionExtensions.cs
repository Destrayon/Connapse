using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnapseCore(this IServiceCollection services)
    {
        // Core registrations (shared services, options validation)
        return services;
    }

    /// <summary>
    /// Validation metadata for the request records declared here. The source generator only
    /// covers types in the assembly that calls AddValidation, so the host calls this as well as
    /// its own AddValidation.
    /// </summary>
    public static IServiceCollection AddCoreValidation(this IServiceCollection services) =>
        services.AddValidation();
}
