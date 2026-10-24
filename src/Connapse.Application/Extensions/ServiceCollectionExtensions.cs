using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Application.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every operation in this assembly, scoped: each surface resolves the same class,
    /// which is what keeps their behaviour the same.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        foreach (Type operation in Operations(typeof(ServiceCollectionExtensions).Assembly))
            services.AddScoped(operation);
        return services;
    }

    /// <summary>The concrete classes carrying [Operation] in an assembly.</summary>
    public static IEnumerable<Type> Operations(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<OperationAttribute>() is not null);
}
