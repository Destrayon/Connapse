using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Application.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every operation in this assembly, scoped: each surface resolves the same class,
    /// which is what keeps their behaviour the same. The facades that group a feature's operations
    /// for a page are registered the same way.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(ServiceCollectionExtensions).Assembly;
        foreach (Type type in Operations(assembly).Concat(Facades(assembly)))
            services.AddScoped(type);
        return services;
    }

    /// <summary>
    /// The classes that only group operations: concrete, not operations themselves, with one
    /// constructor whose every parameter is an operation.
    /// </summary>
    public static IEnumerable<Type> Facades(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }
            && t.GetCustomAttribute<OperationAttribute>() is null
            && t.GetConstructors() is [{ } constructor]
            && constructor.GetParameters() is { Length: > 0 } parameters
            && parameters.All(p => p.ParameterType.GetCustomAttribute<OperationAttribute>() is not null));

    /// <summary>The concrete classes carrying [Operation] in an assembly.</summary>
    public static IEnumerable<Type> Operations(Assembly assembly) =>
        assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<OperationAttribute>() is not null);
}
