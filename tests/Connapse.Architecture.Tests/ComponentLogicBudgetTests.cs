using System.Text;
using System.Text.Json;

namespace Connapse.Architecture.Tests;

/// <summary>
/// Proof test for epic #783, problem P4: business logic lives in pages. Each component with page
/// logic has a committed ceiling for its <c>@code</c> lines, <c>@inject</c> directives and complexity.
/// Growing past a ceiling fails, and so does shrinking below one without lowering it, so the
/// ceilings only ever move down. Small components need no entry.
/// </summary>
[Trait("Category", "Unit")]
public class ComponentLogicBudgetTests
{
    private const string BudgetFile = "component-logic-budgets.json";

    /// <summary>Below these, a component holds too little logic to need a reviewed budget.</summary>
    internal static readonly RazorMetrics.ComponentMetrics SmallComponent = new(CodeLines: 40, Injects: 3, Complexity: 12);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [Fact]
    public void ComponentLogicBudget_MatchesRatchet()
    {
        string components = Path.Combine(RepoPaths.WebProject, "Components");
        var actual = Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories)
            .ToDictionary(
                f => Path.GetRelativePath(components, f).Replace('\\', '/'),
                f => RazorMetrics.MeasureRazor(File.ReadAllText(f)));

        var budgets = JsonSerializer.Deserialize<Dictionary<string, RazorMetrics.ComponentMetrics>>(
            File.ReadAllText(RepoPaths.Baseline(BudgetFile))) ?? [];

        var problems = Compare(actual, budgets).ToList();
        if (problems.Count == 0)
            return;

        var tracked = actual.Where(kv => budgets.ContainsKey(kv.Key) || !FitsSmall(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var message = new StringBuilder();
        foreach (string p in problems) message.AppendLine(p);
        message.AppendLine($"Current values, for review in Baselines/{BudgetFile}:");
        message.AppendLine(JsonSerializer.Serialize(tracked, Json));
        Assert.Fail(message.ToString());
    }

    internal static IEnumerable<string> Compare(
        IReadOnlyDictionary<string, RazorMetrics.ComponentMetrics> actual,
        IReadOnlyDictionary<string, RazorMetrics.ComponentMetrics> budgets)
    {
        foreach (var (file, m) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!budgets.TryGetValue(file, out var b))
            {
                if (!FitsSmall(m))
                    yield return $"+ {file} has page logic over the small-component limit ({m}); move it into operations, or add a reviewed budget.";
                continue;
            }
            if (m.CodeLines > b.CodeLines || m.Injects > b.Injects || m.Complexity > b.Complexity)
                yield return $"+ {file} grew past its budget: {m} > {b}. Move the new logic into an operation.";
            else if (m != b)
                yield return $"- {file} shrank to {m}; lower its budget to match.";
        }
        foreach (string gone in budgets.Keys.Except(actual.Keys).Order(StringComparer.Ordinal))
            yield return $"- {gone} no longer exists; delete its budget.";
    }

    private static bool FitsSmall(RazorMetrics.ComponentMetrics m) =>
        m.CodeLines <= SmallComponent.CodeLines && m.Injects <= SmallComponent.Injects && m.Complexity <= SmallComponent.Complexity;
}
