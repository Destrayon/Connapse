using System.Text;
using System.Text.Json;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P4: business logic lives in pages. Every component with any
/// page logic has a committed ceiling for its C# lines, <c>@inject</c> directives, complexity and
/// markup logic. Growing past a ceiling fails, and so does shrinking below one without lowering it,
/// so ceilings only ever move down. There is no small-component exemption: splitting a page into
/// many small components would otherwise move its logic out of measurement.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class ComponentLogicBudgetTests
{
    private const string BudgetFile = "component-logic-budgets.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [Fact]
    public void ComponentLogicBudget_MatchesRatchet()
    {
        var actual = MeasureAll();
        var budgets = JsonSerializer.Deserialize<Dictionary<string, RazorMetrics.ComponentMetrics>>(
            File.ReadAllText(RepoPaths.Baseline(BudgetFile))) ?? [];

        var problems = Compare(actual, budgets).ToList();
        if (problems.Count == 0)
            return;

        var tracked = actual.Where(kv => !kv.Value.IsEmpty)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var message = new StringBuilder();
        foreach (string p in problems) message.AppendLine(p);
        message.AppendLine($"Current values, for review in Baselines/{BudgetFile}:");
        message.AppendLine(JsonSerializer.Serialize(tracked, Json));
        Assert.Fail(message.ToString());
    }

    /// <summary>Each component's metrics, including its .razor.cs code-behind and any web base class it @inherits.</summary>
    internal static Dictionary<string, RazorMetrics.ComponentMetrics> MeasureAll()
    {
        string components = Path.Combine(RepoPaths.WebProject, "Components");
        var webClasses = RepoPaths.WebCSharpFiles().ToList();
        return Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(components, f).Replace('\\', '/'),
            f =>
            {
                string razor = File.ReadAllText(f);
                var extra = new List<string>();
                if (File.Exists(f + ".cs"))
                    extra.Add(File.ReadAllText(f + ".cs"));
                if (RazorMetrics.InheritedClass(razor) is { } baseClass)
                    extra.AddRange(webClasses.Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Source, $@"\bclass\s+{baseClass}\b"))
                        .Select(c => c.Source));
                return RazorMetrics.MeasureRazor(razor, extra);
            });
    }

    internal static IEnumerable<string> Compare(
        IReadOnlyDictionary<string, RazorMetrics.ComponentMetrics> actual,
        IReadOnlyDictionary<string, RazorMetrics.ComponentMetrics> budgets)
    {
        foreach (var (file, m) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!budgets.TryGetValue(file, out var b))
            {
                if (!m.IsEmpty)
                    yield return $"+ {file} has page logic but no budget ({m}); move it into operations, or add a reviewed budget.";
                continue;
            }
            if (m.CodeLines > b.CodeLines || m.Injects > b.Injects || m.Complexity > b.Complexity || m.MarkupLogic > b.MarkupLogic)
                yield return $"+ {file} grew past its budget: {m} > {b}. Move the new logic into an operation.";
            else if (m != b)
                yield return $"- {file} shrank to {m}; lower its budget to match.";
        }
        foreach (string gone in budgets.Keys.Except(actual.Keys).Order(StringComparer.Ordinal))
            yield return $"- {gone} no longer exists; delete its budget.";
    }
}
