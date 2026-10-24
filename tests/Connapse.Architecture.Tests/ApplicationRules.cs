using System.Reflection;
using System.Xml.Linq;
using Connapse.Application;

namespace Connapse.Architecture.Tests;

/// <summary>Rules for Connapse.Application, the operations every surface calls (epic #783, #831).</summary>
internal static class ApplicationRules
{
    /// <summary>
    /// Every way ASP.NET Core could reach the operations: an assembly the compiled Application
    /// references, a framework or package reference in its project file, or the framework flowing in
    /// from a project it references (Identity carries Microsoft.AspNetCore.App).
    /// </summary>
    public static IEnumerable<string> AspNetCoreDependencies(Assembly assembly, string projectXml)
    {
        foreach (string name in assembly.GetReferencedAssemblies().Select(a => a.Name).OfType<string>().Where(IsAspNetCore).Order(StringComparer.Ordinal))
            yield return $"references {name}";

        var project = XDocument.Parse(projectXml);
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName is "FrameworkReference" or "PackageReference"))
        {
            if ((string?)reference.Attribute("Include") is { } include && IsAspNetCore(include))
                yield return $"{reference.Name.LocalName} {include}";
        }

        if (!project.Descendants().Any(e => e.Name.LocalName == "DisableTransitiveFrameworkReferences"
                && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            yield return "DisableTransitiveFrameworkReferences is not true, so the ASP.NET Core framework flows in from Identity";
    }

    /// <summary>
    /// Operations whose metadata is incomplete: no id, no scope, no risk tier (or one outside the
    /// enum), and ids used by more than one operation. Abstract classes are included, because an
    /// [Operation] there marks nothing AddApplication registers.
    /// </summary>
    public static IEnumerable<string> OperationsMissingMetadata(IEnumerable<Type> types)
    {
        var operations = types
            .Select(t => (Type: t, Attribute: t.GetCustomAttribute<OperationAttribute>(inherit: false)))
            .Where(o => o.Attribute is not null)
            .Select(o => (o.Type, Attribute: o.Attribute!))
            .ToList();

        foreach (var (type, attribute) in operations)
        {
            string name = type.FullName ?? type.Name;
            if (string.IsNullOrWhiteSpace(attribute.Id))
                yield return $"{name}: no id";
            if (string.IsNullOrWhiteSpace(attribute.Scope))
                yield return $"{name}: no Scope";
            if (attribute.Risk == RiskTier.Unspecified || !Enum.IsDefined(attribute.Risk))
                yield return $"{name}: no Risk";
            if (type.IsAbstract)
                yield return $"{name}: abstract, so it is never registered";
        }

        foreach (var shared in operations.Where(o => !string.IsNullOrWhiteSpace(o.Attribute.Id)).GroupBy(o => o.Attribute.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{shared.Key}: on {string.Join(", ", shared.Select(o => o.Type.FullName).Order(StringComparer.Ordinal))}";
    }

    private static bool IsAspNetCore(string name) => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);
}
