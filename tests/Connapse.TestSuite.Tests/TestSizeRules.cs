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

    /// <summary>
    /// LiveAws runs in no CI step, by design: it needs real AWS. Only these classes may use it, so it
    /// can't become a way to keep a new test out of CI.
    /// </summary>
    public static readonly string[] LiveAwsClasses = ["RolesAnywhereLiveAwsTests"];

    /// <summary>One problem per offending test method, as "file: Class.Method: reason".</summary>
    public static IEnumerable<string> Problems(string relativePath, SyntaxNode root) =>
        Problems(relativePath, root, TestSources.TestAttributeNames.Value, TestSources.PartialClassTraits.Value);

    /// <param name="partialTraits">
    /// Class-level traits of every partial class, combined across its declarations: xUnit gives a
    /// test the traits of all of them, so checking each declaration alone could miss a conflict.
    /// </param>
    public static IEnumerable<string> Problems(string relativePath, SyntaxNode root, IReadOnlyCollection<string> testAttributes,
        IReadOnlyDictionary<string, List<(string Name, string Value)>>? partialTraits = null)
    {
        foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var classTraits = partialTraits is not null && cls.Modifiers.Any(m => m.ValueText == "partial")
                && partialTraits.TryGetValue(TestSources.ClassKey(relativePath, cls), out var combined)
                ? combined
                : TestSources.Traits(cls.AttributeLists).ToList();
            foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>().Where(m => TestSources.IsTestMethod(m, testAttributes)))
            {
                var traits = classTraits.Concat(TestSources.Traits(method.AttributeLists)).ToList();
                string? reason = Check(traits) ?? CheckLiveAws(cls.Identifier.ValueText, traits);
                if (reason is not null)
                    yield return $"{relativePath}: {cls.Identifier.ValueText}.{method.Identifier.ValueText}: {reason}";
            }
        }
    }

    internal static string? Check(IReadOnlyList<(string Name, string Value)> traits)
    {
        if (traits.Any(t => t.Name == "<non-literal>"))
            return "a Trait argument isn't a string literal, so its tier can't be read";

        var categories = traits.Where(t => t.Name == "Category").Select(t => t.Value).ToList();
        var sizes = traits.Where(t => t.Name == "Size").Select(t => t.Value).ToList();

        if (categories.Count != 1)
            return $"needs exactly one Category trait, has {categories.Count}";
        if (!Categories.Contains(categories[0]))
            return $"unknown Category '{categories[0]}'";
        if (sizes.Count > 1)
            return $"has {sizes.Count} Size traits; traits add up, so keep one";
        if (categories.Count == 1 && sizes.Count == 0 && traits.Count(t => t.Name == "Category") > 1)
            return "repeats its Category trait";
        if (sizes.Count == 1 && sizes[0] is not ("Medium" or "Large"))
            return $"Size '{sizes[0]}' isn't Medium or Large; Small is the default and takes no trait";
        if (sizes.Count == 1 && sizes[0] == "Medium" && categories[0] != "Unit")
            return "Size=Medium only applies to unit tests; integration tests are Medium already";
        return null;
    }

    internal static string? CheckLiveAws(string className, IReadOnlyList<(string Name, string Value)> traits) =>
        traits.Any(t => t is ("Category", "LiveAws")) && !LiveAwsClasses.Contains(className)
            ? "Category=LiveAws runs in no CI step and is reserved for the listed live-AWS classes"
            : null;
}
