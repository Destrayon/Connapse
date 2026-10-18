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
            .SelectMany(f => FixedDelays(f.Root).GroupBy(Member).Select(g => $"{f.RelativePath}: {g.Key}: {g.Count()}"));

        BaselineList.AssertMatches("fixed-delays.json", found,
            "Wait for the condition instead: poll it with a deadline.");
    }

    [Fact]
    public void FixedDelays_CountsSleepsAndPacingButNotPollingOrHangSimulation()
    {
        const string source = """
            class C
            {
                async Task M(CancellationToken ct)
                {
                    await Task.Delay(500);                                  // fixed
                    Thread.Sleep(TimeSpan.FromSeconds(1));                  // fixed
                    await Task.Delay(500, CancellationToken.None);          // fixed: the token can't cancel
                    await Task.Delay(250, ct);                              // fixed: a finite wait, cancellable or not
                    while (!done) { await Task.Delay(200); }                // polling
                    for (int i = 0; i < 3; i++) Thread.Sleep(10);           // fixed: pacing, not waiting for anything
                    int attempt = 0;
                    while (attempt++ < 10) Thread.Sleep(500);               // fixed: a counted while waits for nothing
                    while (sw.Elapsed < limit) { if (Ready()) break; await Task.Delay(50); }  // polling: the body checks
                    for (int i = 0; i < 50; i++) { if (Ready()) return; await Task.Delay(100); } // polling
                    for (int i = 0; i < 20 && !Ready(); i++) await Task.Delay(50);            // polling
                    while (true)
                    {
                        if (Ready()) { await Task.Delay(500); return; }     // fixed: after the condition succeeded
                        await Task.Delay(200);                              // polling
                    }
                }

                Task Hang(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);           // a fake that never answers
                Task Hang2(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
                Func<Task> f = () => Task.Delay(100);                                       // fixed
            }
            """;
        var delays = FixedDelays(CSharpSyntaxTree.ParseText(source).GetRoot()).ToList();

        delays.Should().HaveCount(8);
        delays.GroupBy(Member).Select(g => $"{g.Key}: {g.Count()}").Should().BeEquivalentTo(["M: 7", "f: 1"]);
    }

    /// <summary>
    /// Task.Delay and Thread.Sleep calls that wait a fixed time: not polling inside a loop, and not
    /// an infinite wait standing in for a hung dependency. A wait passed a cancellation token still
    /// counts unless it is infinite: CancellationToken.None can't cancel, and a finite wait waits.
    /// </summary>
    internal static IEnumerable<InvocationExpressionSyntax> FixedDelays(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call =>
            call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name, Expression: var target }
            && ((name == "Delay" && target.ToString().EndsWith("Task", StringComparison.Ordinal))
                || (name == "Sleep" && target.ToString().EndsWith("Thread", StringComparison.Ordinal)))
            && call.ArgumentList.Arguments.Count >= 1
            && !IsInfinite(call.ArgumentList.Arguments[0].Expression)
            && !IsPolling(call));

    private static bool IsInfinite(ExpressionSyntax delay) =>
        delay.ToString() is "Timeout.Infinite" or "Timeout.InfiniteTimeSpan" or "-1" or "System.Threading.Timeout.Infinite";

    /// <summary>
    /// Inside a loop, and not the last thing before leaving it: a wait followed by return, break or
    /// throw in its own block happens after the condition succeeded, so it is a fixed sleep.
    /// </summary>
    private static bool IsPolling(SyntaxNode call)
    {
        var loop = EnclosingLoop(call);
        if (loop is null || !WaitsOnACondition(loop))
            return false;
        var statement = call.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent is BlockSyntax block)
        {
            var after = block.Statements.SkipWhile(st => st != statement).Skip(1);
            if (after.Any(st => st is ReturnStatementSyntax or BreakStatementSyntax or ThrowStatementSyntax))
                return false;
        }
        return true;
    }

    private static StatementSyntax? EnclosingLoop(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            if (n is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax)
                return (StatementSyntax)n;
            if (n is MemberDeclarationSyntax or LocalFunctionStatementSyntax)
                return null;
        }
        return null;
    }

    /// <summary>
    /// Polling means the loop can end on a condition: a while or do with a real condition, or a body
    /// that leaves (return, break, throw) under an if or switch. A for or foreach that only counts,
    /// sleeping each pass, is pacing: a fixed wait multiplied, not a wait for anything.
    /// </summary>
    /// <summary>
    /// A loop condition that only counts or measures time: one comparison (attempt++ &lt; 10,
    /// i &lt; 3, sw.Elapsed &lt; timeout) with no call and no &amp;&amp; or ||. Such a loop waits a fixed
    /// time unless its body checks something; !done, status != Ready or Ready() are readiness checks.
    /// </summary>
    private static bool IsCountOnly(ExpressionSyntax condition) =>
        condition is BinaryExpressionSyntax b
        && (b.IsKind(SyntaxKind.LessThanExpression) || b.IsKind(SyntaxKind.LessThanOrEqualExpression)
            || b.IsKind(SyntaxKind.GreaterThanExpression) || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
        && !condition.DescendantNodes().Any(n => n is InvocationExpressionSyntax
            || n.IsKind(SyntaxKind.LogicalAndExpression) || n.IsKind(SyntaxKind.LogicalOrExpression));

    private static bool WaitsOnACondition(StatementSyntax loop)
    {
        var condition = loop switch
        {
            WhileStatementSyntax w => w.Condition,
            DoStatementSyntax d => d.Condition,
            ForStatementSyntax f => f.Condition,
            _ => null,
        };
        if (condition is not null && !condition.IsKind(SyntaxKind.TrueLiteralExpression) && !IsCountOnly(condition))
            return true;
        return loop.DescendantNodes().OfType<StatementSyntax>()
            .Where(st => st is IfStatementSyntax or SwitchStatementSyntax)
            .Any(st => st.DescendantNodes().Any(x => x is ReturnStatementSyntax or BreakStatementSyntax or ThrowStatementSyntax));
    }

    /// <summary>The method, local function, property or field a delay sits in, so entries don't move with line numbers.</summary>
    internal static string Member(SyntaxNode node)
    {
        foreach (var a in node.Ancestors())
        {
            switch (a)
            {
                case LocalFunctionStatementSyntax f: return f.Identifier.ValueText;
                case MethodDeclarationSyntax m: return m.Identifier.ValueText;
                case ConstructorDeclarationSyntax c: return c.Identifier.ValueText;
                case PropertyDeclarationSyntax pr: return pr.Identifier.ValueText;
                case FieldDeclarationSyntax fd: return fd.Declaration.Variables.First().Identifier.ValueText;
            }
        }
        return "(top level)";
    }
}
