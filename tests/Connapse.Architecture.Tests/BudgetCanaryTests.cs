using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>Proves the component logic measurement and ratchet comparison still behave.</summary>
[Trait("Category", "Unit")]
public class BudgetCanaryTests
{

    [Fact]
    public void LogicBudget_MeasuresCodeBlocksAndIgnoresBracesInStrings()
    {
        const string razor = """
            @page "/planted"
            @inject ISourceStore Sources
            @inject ILogger<Planted> Logger
            <p>@if (show) { <b>markup is not counted</b> }</p>
            @code {
                private string brace = "}{";
                private char open = '{';
                // a } in a comment
                private async Task SaveAsync()
                {
                    if (brace.Length > 0 && open != ' ') { await Task.Yield(); }
                    foreach (var c in brace) { }
                }
            }
            """;
        var m = RazorMetrics.MeasureRazor(razor);

        m.Injects.Should().Be(2);
        m.CodeLines.Should().Be(8);
        // one method, one if, one &&, one foreach
        m.Complexity.Should().Be(4);
    }

    [Fact]
    public void LogicBudget_FailsOnGrowthShrinkageAndUntrackedLogic()
    {
        var budgets = new Dictionary<string, RazorMetrics.ComponentMetrics>
        {
            ["Grew.razor"] = new(10, 1, 2),
            ["Shrank.razor"] = new(10, 1, 2),
            ["Deleted.razor"] = new(10, 1, 2),
        };
        var actual = new Dictionary<string, RazorMetrics.ComponentMetrics>
        {
            ["Grew.razor"] = new(11, 1, 2),
            ["Shrank.razor"] = new(9, 1, 2),
            ["Big.razor"] = new(500, 9, 90),
            ["Tiny.razor"] = new(5, 1, 1),
        };

        var problems = ComponentLogicBudgetTests.Compare(actual, budgets).ToList();

        problems.Should().HaveCount(4);
        problems.Should().Contain(p => p.StartsWith("+ Grew.razor grew"))
            .And.Contain(p => p.StartsWith("- Shrank.razor shrank"))
            .And.Contain(p => p.StartsWith("+ Big.razor has page logic"))
            .And.Contain(p => p.StartsWith("- Deleted.razor no longer exists"));
    }
}
