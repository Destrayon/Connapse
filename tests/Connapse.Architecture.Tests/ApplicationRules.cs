using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Connapse.Application;

namespace Connapse.Architecture.Tests;

/// <summary>Rules for Connapse.Application, the operations every surface calls (epic #783, #831).</summary>
internal static class ApplicationRules
{
    /// <summary>
    /// Every way ASP.NET Core could reach the operations' compiler: an assembly the compiled
    /// Application references, a framework reference in its project file, the framework flowing in
    /// from a project it references (Identity carries Microsoft.AspNetCore.App), or an ASP.NET Core
    /// package in its restore graph that it can compile against. A package reference is allowed only
    /// when it excludes compile assets, which is how the packages Identity and Storage bring in are
    /// kept out.
    /// </summary>
    public static IEnumerable<string> AspNetCoreDependencies(Assembly assembly, string projectXml, string assetsJson)
    {
        foreach (string name in assembly.GetReferencedAssemblies().Select(a => a.Name).OfType<string>().Where(IsAspNetCore).Order(StringComparer.Ordinal))
            yield return $"references {name}";

        var project = XDocument.Parse(projectXml);
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "FrameworkReference"))
        {
            if ((string?)reference.Attribute("Include") is { } include && IsAspNetCore(include))
                yield return $"FrameworkReference {include}";
        }
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            if ((string?)reference.Attribute("Include") is { } include && IsAspNetCore(include) && !ExcludesCompile((string?)reference.Attribute("ExcludeAssets")))
                yield return $"PackageReference {include} without ExcludeAssets=\"compile\"";
        }

        if (!project.Descendants().Any(e => e.Name.LocalName == "DisableTransitiveFrameworkReferences"
                && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            yield return "DisableTransitiveFrameworkReferences is not true, so the ASP.NET Core framework flows in from Identity";

        foreach (string package in CompilablePackages(assetsJson).Where(IsAspNetCore).Order(StringComparer.Ordinal))
            yield return $"compiles against package {package}";
    }

    /// <summary>
    /// Packages in the restore graph whose compile assets reach this project: every package, minus
    /// those a direct reference keeps the Compile asset from.
    /// </summary>
    private static IEnumerable<string> CompilablePackages(string assetsJson)
    {
        using var assets = JsonDocument.Parse(assetsJson);
        var root = assets.RootElement;
        var direct = root.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(f => f.Value.TryGetProperty("dependencies", out var deps) ? deps.EnumerateObject() : [])
            .ToDictionary(d => d.Name, d => d.Value.TryGetProperty("include", out var inc) ? inc.GetString() ?? "" : "All", StringComparer.OrdinalIgnoreCase);

        foreach (var library in root.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;
            string name = library.Name.Split('/')[0];
            if (direct.TryGetValue(name, out string? include) && !IncludesCompile(include))
                continue;
            yield return name;
        }
    }

    private static bool IncludesCompile(string include) =>
        include.Split(',', StringSplitOptions.TrimEntries).Any(a => a is "Compile" or "All");

    private static bool ExcludesCompile(string? excludeAssets) =>
        excludeAssets is not null && excludeAssets.Split([',', ';'], StringSplitOptions.TrimEntries)
            .Any(a => a.Equals("compile", StringComparison.OrdinalIgnoreCase) || a.Equals("all", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Operations whose metadata is incomplete: no id, no scope, no risk tier (or one outside the
    /// enum), ids used by more than one operation, and operations AddApplication would never
    /// register: abstract classes, or classes outside <paramref name="home"/>.
    /// </summary>
    public static IEnumerable<string> OperationsMissingMetadata(IEnumerable<Type> types, Assembly home)
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
            if (type.Assembly != home)
                yield return $"{name}: outside {home.GetName().Name}, so AddApplication never registers it";
        }

        foreach (var shared in operations.Where(o => !string.IsNullOrWhiteSpace(o.Attribute.Id)).GroupBy(o => o.Attribute.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{shared.Key}: on {string.Join(", ", shared.Select(o => o.Type.FullName).Order(StringComparer.Ordinal))}";
    }

    /// <summary>
    /// Failure values of the status enums operations return that REST could not map: no
    /// [Failure], a status outside 4xx/5xx, a slug with no entry in docs/problems.md, or one slug
    /// given two different statuses or titles.
    /// </summary>
    public static IEnumerable<string> FailuresWithoutProblem(IEnumerable<Type> types, ISet<string> documentedSlugs)
    {
        var statusEnums = types
            .Where(t => t.IsDefined(typeof(OperationAttribute), inherit: false))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => StatusOf(m.ReturnType))
            .OfType<Type>()
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        var seen = new Dictionary<string, (int Status, string Title, string Where)>(StringComparer.Ordinal);
        foreach (var status in statusEnums)
        {
            foreach (var field in status.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (Convert.ToInt64(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                    continue;
                string name = $"{status.FullName}.{field.Name}";
                var failure = field.GetCustomAttribute<FailureAttribute>();
                if (failure is null)
                {
                    yield return $"{name}: no [Failure]";
                    continue;
                }
                if (failure.HttpStatus is < 400 or > 599)
                    yield return $"{name}: HTTP {failure.HttpStatus} is not a failure status";
                if (!documentedSlugs.Contains(failure.Slug))
                    yield return $"{name}: slug {failure.Slug} has no entry in docs/problems.md";
                if (seen.TryGetValue(failure.Slug, out var first) && (first.Status != failure.HttpStatus || first.Title != failure.Title))
                    yield return $"{name}: slug {failure.Slug} already means HTTP {first.Status} \"{first.Title}\" on {first.Where}";
                else
                    seen.TryAdd(failure.Slug, (failure.HttpStatus, failure.Title, name));
            }
        }
    }

    /// <summary>The status enum of an Attempt, or of a Task or ValueTask of one.</summary>
    private static Type? StatusOf(Type returnType)
    {
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() is var open
            && (open == typeof(Task<>) || open == typeof(ValueTask<>)))
            returnType = returnType.GetGenericArguments()[0];
        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Attempt<,>)
            ? returnType.GetGenericArguments()[1]
            : null;
    }

    /// <summary>The slugs docs/problems.md documents: each is a second-level heading.</summary>
    public static ISet<string> DocumentedSlugs(string problemsMarkdown) =>
        problemsMarkdown.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .Select(l => l[3..].Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static bool IsAspNetCore(string name) => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);
}
