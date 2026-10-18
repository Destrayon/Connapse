using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Connapse.TestSuite.Tests;

/// <summary>
/// Proof test for epic #783, problem P10: a fixed sleep waits its full length every run, and
/// waits too little on a slow runner. Waiting for a condition, by polling with a deadline, is
/// both faster and steadier. A delay inside a loop is polling, and one passed a cancellation
/// token is a fake standing in for a slow or hung dependency, so neither counts.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class FixedDelayTests
{
    [Fact]
    public void TestCode_FixedDelays_MatchList()
    {
        var found = TestSources.Files()
            .Select(f => (f.RelativePath, Count: FixedDelays(f.Root).Count()))
            .Where(f => f.Count > 0)
            .Select(f => $"{f.RelativePath}: {f.Count}");

        BaselineList.AssertMatches("fixed-delays.json", found,
            "Wait for the condition instead: poll it with a deadline.");
    }

    [Fact]
    public void FixedDelays_IgnoresPollingAndCancellableWaits()
    {
        const string source = """
            class C
            {
                async Task M(CancellationToken ct)
                {
                    await Task.Delay(500);
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    while (!done) { await Task.Delay(200); }
                    for (int i = 0; i < 3; i++) Thread.Sleep(10);
                    await Task.Delay(Timeout.Infinite, ct);
                    Func<Task> f = () => Task.Delay(100);
                }
            }
            """;
        FixedDelays(CSharpSyntaxTree.ParseText(source).GetRoot()).Should().HaveCount(3);
    }

    internal static IEnumerable<InvocationExpressionSyntax> FixedDelays(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name, Expression: var target }
            && ((name == "Delay" && target.ToString().EndsWith("Task", StringComparison.Ordinal))
                || (name == "Sleep" && target.ToString().EndsWith("Thread", StringComparison.Ordinal)))
            && call.ArgumentList.Arguments.Count == 1
            && !InsideLoop(call));

    private static bool InsideLoop(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax)
                return true;
            if (n is MemberDeclarationSyntax or LocalFunctionStatementSyntax)
                return false;
        }
        return false;
    }
}
