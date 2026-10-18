using FluentAssertions;

namespace Connapse.Architecture.Tests;

/// <summary>Proves the component logic measurement and ratchet comparison still see what they must.</summary>
[Trait("Category", "Unit")]
public class BudgetCanaryTests
{
    [Fact]
    public void LogicBudget_MeasuresCodeBlocksAndIgnoresBracesInStrings()
    {
        const string razor = """""
            @page "/planted"
            @inject ISourceStore Sources
            @inject ILogger<Planted> Logger
            @code {
                private string brace = "}{";
                private char open = '{';
                private string raw = """"
                    a four-quote raw string with a "\"" and a } inside
                    """";
                // a } in a comment
                private async Task SaveAsync()
                {
                    if (brace.Length > 0 && open != ' ') { await Task.Yield(); }
                    foreach (var c in brace) { }
                }
            }
            """"";
        var m = RazorMetrics.MeasureRazor(razor);

        m.Injects.Should().Be(2);
        m.CodeLines.Should().Be(11);
        // one method, one if, one &&, one foreach
        m.Complexity.Should().Be(4);
        m.MarkupLogic.Should().Be(0);
    }

    [Fact]
    public void LogicBudget_CountsMarkupLogicAndCodeBehind()
    {
        const string razor = """
            @if (items.Count > 0 && !loading)
            {
                @foreach (var item in items) { <button @onclick="() => Select(item)">@(item.Name ?? "-")</button> }
            }
            else if (failed) { <p>Failed</p> }
            @switch (mode) { case Mode.A: <p>A</p> break; }
            """;
        const string codeBehind = """
            public partial class Planted
            {
                private void Select(Item item) { if (item is null) return; }
            }
            """;
        var m = RazorMetrics.MeasureRazor(razor, [codeBehind]);

        // @if, &&, @foreach, =>, ??, else if, @switch, case
        m.MarkupLogic.Should().Be(8);
        // one method, one if, from the code-behind
        m.Complexity.Should().Be(2);
        // four code-behind lines, plus the handler's call to Select
        m.CodeLines.Should().Be(5);
    }

    [Fact]
    public void LogicBudget_FailsOnGrowthShrinkageAndAnyUnbudgetedLogic()
    {
        var budgets = new Dictionary<string, RazorMetrics.ComponentMetrics>
        {
            ["Grew.razor"] = new(10, 1, 2, 0),
            ["Shrank.razor"] = new(10, 1, 2, 0),
            ["Deleted.razor"] = new(10, 1, 2, 0),
        };
        var actual = new Dictionary<string, RazorMetrics.ComponentMetrics>
        {
            ["Grew.razor"] = new(10, 1, 2, 1),
            ["Shrank.razor"] = new(9, 1, 2, 0),
            ["Big.razor"] = new(500, 9, 90, 40),
            ["Tiny.razor"] = new(5, 1, 1, 0),
            ["Markup.razor"] = new(0, 0, 0, 0),
        };

        var problems = ComponentLogicBudgetTests.Compare(actual, budgets).ToList();

        problems.Should().HaveCount(5);
        problems.Should().Contain(p => p.StartsWith("+ Grew.razor grew"))
            .And.Contain(p => p.StartsWith("- Shrank.razor shrank"))
            .And.Contain(p => p.StartsWith("+ Big.razor has page logic"))
            .And.Contain(p => p.StartsWith("+ Tiny.razor has page logic"), "a split-off small component still needs a reviewed budget")
            .And.Contain(p => p.StartsWith("- Deleted.razor no longer exists"));
    }

    [Fact]
    public void LogicBudget_CountsStatementBlocksAndTernaries()
    {
        const string razor = """
            @{ var choices = AllowedScopes(); if (choices.Count == 0) { choices = Defaults(); } }
            <span class="@(active ? "on" : "off")">@(count > 1 ? $"{count} items" : "1 item")</span>
            """;
        var m = RazorMetrics.MeasureRazor(razor);

        // the statement block: one line, one if
        m.CodeLines.Should().Be(1);
        m.Complexity.Should().Be(1);
        // two ternaries in @( ) expressions
        m.MarkupLogic.Should().Be(2);
    }

    [Fact]
    public void LogicBudget_MeasuresTheBodiesOfMarkupHandlers()
    {
        const string small = """
            <input ValueChanged="@((string? v) => { replaceClientId = v; })" />
            <button @onclick="() => Save()">Save</button>
            """;
        const string grown = """
            <input ValueChanged="@((string? v) => { replaceClientId = v; if (v is null) { error = "required"; } })" />
            <button @onclick="() => { Save(); Close(); }">Save</button>
            """;
        var before = RazorMetrics.MeasureRazor(small);
        var after = RazorMetrics.MeasureRazor(grown);

        after.MarkupLogic.Should().Be(before.MarkupLogic, "both have the same two arrows");
        // grown handler bodies: +2 statements in the first (if, its assignment); the second goes from
        // one call in an expression body to two statements, +1
        after.CodeLines.Should().Be(before.CodeLines + 3);
        after.Complexity.Should().Be(before.Complexity + 1, "one added if");
    }

    [Fact]
    public void LogicBudget_MeasuresSingleQuotedMarkupHandlers()
    {
        const string small = """
            <button @onclick='() => activeTab = "embedding"'>Embedding</button>
            """;
        const string grown = """
            <button @onclick='() => { if (isAdmin) activeTab = "embedding"; }'>Embedding</button>
            """;
        var before = RazorMetrics.MeasureRazor(small);
        var after = RazorMetrics.MeasureRazor(grown);

        after.CodeLines.Should().BeGreaterThan(before.CodeLines);
        after.Complexity.Should().Be(before.Complexity + 1, "one added if");
    }

    [Fact]
    public void LogicBudget_MeasuresStatementsInsideMarkupControlBlocks()
    {
        const string small = """
            @foreach (var scope in Scopes)
            {
                bool confirmed = scope.Confirmed;
                <li>@scope.Name</li>
            }
            """;
        const string grown = """
            @foreach (var scope in Scopes)
            {
                bool confirmed = scope.Confirmed;
                if (scope.Optional) { confirmed = false; }
                <li>@scope.Name</li>
            }
            """;
        var before = RazorMetrics.MeasureRazor(small);
        var after = RazorMetrics.MeasureRazor(grown);

        before.CodeLines.Should().Be(1, "the declaration is a statement");
        after.CodeLines.Should().Be(2);
        after.Complexity.Should().Be(before.Complexity + 1, "one added if");
    }

    [Fact]
    public void LogicBudget_IgnoresCssAndEntitiesInMarkup()
    {
        const string razor = """
            <style>
                .card { color: red; }
                margin: 0;
            </style>
            <p>Saved&nbsp;</p>
            Saved &amp;
            """;
        RazorMetrics.MeasureRazor(razor).CodeLines.Should().Be(0);
    }

    [Fact]
    public void LogicBudget_CountsCallsInExpressionBodiedHandlers()
    {
        const string small = """<button @onclick="() => DeleteAsync(id)">Delete</button>""";
        const string grown = """<button @onclick="() => DeleteAsync(id).ContinueWith(NotifyAdmin)">Delete</button>""";

        RazorMetrics.MeasureRazor(grown).CodeLines.Should().Be(RazorMetrics.MeasureRazor(small).CodeLines + 1);
    }

    [Fact]
    public void LogicBudget_MeasuresAsyncMarkupHandlers()
    {
        const string small = """<button @onclick="async () => { await SaveAsync(); }">Save</button>""";
        const string grown = """<button @onclick="async () => { await SaveAsync(); if (ok) { await NotifyAsync(); } }">Save</button>""";
        var before = RazorMetrics.MeasureRazor(small);
        var after = RazorMetrics.MeasureRazor(grown);

        after.CodeLines.Should().Be(before.CodeLines + 2, "an if and its call");
        after.Complexity.Should().Be(before.Complexity + 1);
    }

    [Fact]
    public void LogicBudget_MeasuresStatementsSharingALineWithMarkup()
    {
        const string small = """@if (saving) { <span>Saving</span> }""";
        const string grown = """@if (saving) { AuditSave(); <span>Saving</span> }""";

        RazorMetrics.MeasureRazor(small).CodeLines.Should().Be(0);
        RazorMetrics.MeasureRazor(grown).CodeLines.Should().Be(1);
    }
}
