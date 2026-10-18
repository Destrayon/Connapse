using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.TestSuite.Tests;

/// <summary>
/// The suite's three tiers, derived from traits so only the exceptions carry a Size:
/// Small is Category=Unit with no Size; Medium is Category=Integration, or Size=Medium on a unit
/// test (200 ms to 5 s); Large is Size=Large (over 5 s, models or child processes). Traits add
/// up in xUnit, so an assembly default plus a class override would give a test two sizes, which is
/// why there is no default.
/// </summary>
internal static class TestSizeRules
{
    public static readonly string[] Categories = ["Unit", "Integration", "LiveAws"];

    /// <summary>One problem per offending test method, as "file: Class.Method: reason".</summary>
    public static IEnumerable<string> Problems(string relativePath, SyntaxNode root)
    {
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var classTraits = TestSources.Traits(cls.AttributeLists).ToList();
            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>().Where(TestSources.IsTestMethod))
            {
                var traits = classTraits.Concat(TestSources.Traits(method.AttributeLists)).ToList();
                string? reason = Check(traits);
                if (reason is not null)
                    yield return $"{relativePath}: {cls.Identifier.ValueText}.{method.Identifier.ValueText}: {reason}";
            }
        }
    }

    internal static string? Check(IReadOnlyList<(string Name, string Value)> traits)
    {
        if (traits.Any(t => t.Name == "<non-literal>"))
            return "a Trait argument isn't a string literal, so its tier can't be read";

        var categories = traits.Where(t => t.Name == "Category").Select(t => t.Value).Distinct().ToList();
        var sizes = traits.Where(t => t.Name == "Size").Select(t => t.Value).Distinct().ToList();

        if (categories.Count != 1)
            return $"needs exactly one Category trait, has {categories.Count}";
        if (!Categories.Contains(categories[0]))
            return $"unknown Category '{categories[0]}'";
        if (sizes.Count > 1)
            return $"has {sizes.Count} Size traits; traits add up, so keep one";
        if (sizes.Count == 1 && sizes[0] is not ("Medium" or "Large"))
            return $"Size '{sizes[0]}' isn't Medium or Large; Small is the default and takes no trait";
        if (sizes.Count == 1 && sizes[0] == "Medium" && categories[0] != "Unit")
            return "Size=Medium only applies to unit tests; integration tests are Medium already";
        return null;
    }
}
