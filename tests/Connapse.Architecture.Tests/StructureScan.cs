using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Reads dependencies through reflection over what the compiler keeps in every build configuration:
/// <c>[Inject]</c> properties on components, and the parameters of every method an endpoint or MCP
/// tool class declares, including the methods its lambdas compile to. ArchUnitNET's IL reading was
/// rejected because its released version drops dependencies inside async methods in Release builds.
/// </summary>
internal static class StructureScan
{
    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static Assembly WebAssembly => typeof(Connapse.Web.Mcp.McpTools).Assembly;

    /// <summary>Data-access types: anything named *Store, EF context factories and DbContexts, also inside generic wrappers.</summary>
    public static bool IsStore(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.IDbContextFactory`1")
            return true;
        for (Type? b = t; b is not null; b = b.BaseType)
            if (b.FullName == "Microsoft.EntityFrameworkCore.DbContext")
                return true;
        if (t.IsGenericType && t.GetGenericArguments().Any(IsStore))
            return true;
        if (t.IsArray && t.GetElementType() is { } element && IsStore(element))
            return true;
        return t.Namespace?.StartsWith("Connapse", StringComparison.Ordinal) == true
            && t.Name.EndsWith("Store", StringComparison.Ordinal);
    }

    public static bool IsServiceLocator(Type t) =>
        t.FullName is "System.IServiceProvider"
            or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
            or "Microsoft.Extensions.DependencyInjection.IServiceScope";

    public static IEnumerable<Type> Components(Assembly assembly) =>
        SafeTypes(assembly).Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t));

    /// <summary>Top-level classes that map routes or declare MCP tools.</summary>
    public static IEnumerable<Type> SurfaceHandlers(Assembly assembly, params string[] namespaces) =>
        SafeTypes(assembly).Where(t => t.DeclaringType is null && t.IsClass && namespaces.Contains(t.Namespace));

    /// <summary>What a component receives from DI: [Inject] properties and constructor parameters.</summary>
    public static IEnumerable<Type> ComponentDependencies(Type component) =>
        InjectedTypes(component).Concat(component.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));

    public static IEnumerable<Type> InjectedTypes(Type component)
    {
        for (Type? t = component; t is not null && t != typeof(ComponentBase); t = t.BaseType)
            foreach (var p in t.GetProperties(Everything))
                if (p.GetCustomAttribute<InjectAttribute>() is not null)
                    yield return p.PropertyType;
    }

    /// <summary>Parameter types of every method in the class and its compiler-generated nested types.</summary>
    public static IEnumerable<Type> HandlerParameterTypes(Type handlerClass)
    {
        foreach (var type in SelfAndNested(handlerClass))
            foreach (var m in type.GetMethods(Everything))
                foreach (var p in m.GetParameters())
                    yield return p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
    }

    public static string Display(Type t) =>
        t.IsGenericType
            ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Display))}>"
            : t.Name;

    /// <summary>Razor components compile to their file name; nested display classes never surface here.</summary>
    public static string Owner(Type t) => t.Name;

    private static IEnumerable<Type> SelfAndNested(Type t)
    {
        yield return t;
        foreach (var nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var inner in SelfAndNested(nested))
                yield return inner;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }
}
