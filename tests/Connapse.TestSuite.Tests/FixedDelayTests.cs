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
                    for (int n = 0; n < 10 && sw.Elapsed < limit; n++) await Task.Delay(500); // fixed: two bounds, no check
                    while (status.QueueDepth > 0) await Task.Delay(200);                      // polling: live state
                    while (pending > 0) { pending = await CountAsync(); await Task.Delay(100); } // polling: re-read each pass
                    int left = 5;
                    while (left > 0) { left--; await Task.Delay(100); }                       // fixed: a countdown
                    while (sw.Elapsed < cap!.Value) await Task.Delay(100);                     // fixed: a null-forgiven limit
                    while (tries! < 10) { tries++; await Task.Delay(500); }                    // fixed: a null-forgiven counter
                    while (!Ready()) { _ = Task.Run(async () => await Task.Delay(500)); await Task.Delay(50); } // the callback's is fixed, the poll's isn't
                    for (int k = 0; k < 3; k++) { _ = Task.Run(() => { if (!Ready()) return; Signal(); }); await Task.Delay(500); } // fixed: the callback's return leaves only it
                    for (int j = 0; j < 3; j++) { switch (status) { case 1: break; } await Task.Delay(500); } // fixed: the break leaves the switch
                    while (!Ready()) { switch (phase) { case 1: { await Task.Delay(100); break; } } } // polling: that break leaves the case
                    while (done < expected) { done++; expected += await NewJobsAsync(); await Task.Delay(100); } // polling: the limit moves
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

        delays.Should().HaveCount(15);
        delays.GroupBy(Member).Select(g => $"{g.Key}: {g.Count()}").Should().BeEquivalentTo(["M: 14", "f: 1"]);
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
            if (after.Any(st => st is ReturnStatementSyntax or ThrowStatementSyntax || BreaksOut(st, loop)))
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
            // A lambda runs on its own schedule (Task.Run(async () => ...)): a loop around it doesn't pace it.
            if (n is MemberDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                return null;
        }
        return null;
    }

    /// <summary>
    /// Polling means the loop can end on a condition: a while, do or for whose condition checks
    /// something besides a count or the clock, or a body that leaves (return, break, throw) under an
    /// if or switch. A loop that only counts or times itself, sleeping each pass, is pacing: a fixed
    /// wait multiplied, not a wait for anything.
    /// </summary>
    private static bool WaitsOnACondition(StatementSyntax loop)
    {
        var condition = loop switch
        {
            WhileStatementSyntax w => w.Condition,
            DoStatementSyntax d => d.Condition,
            ForStatementSyntax f => f.Condition,
            _ => null,
        };
        if (condition is not null && !condition.IsKind(SyntaxKind.TrueLiteralExpression) && !IsBound(condition, loop))
            return true;
        // An exit under an if or switch that leaves this loop: not one inside a lambda or local
        // function, which leaves only that function (Task.Run(() => { if (!Ready()) return; })).
        return loop.DescendantNodes().OfType<StatementSyntax>()
            .Where(st => st is IfStatementSyntax or SwitchStatementSyntax)
            .Any(st => st.DescendantNodes().Any(x => (x is ReturnStatementSyntax or ThrowStatementSyntax || BreaksOut(x, loop))
                && !x.Ancestors().TakeWhile(a => a != loop).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)));
    }

    /// <summary>
    /// A condition that only counts or measures time: comparisons, joined by &amp;&amp; or ||, each of a
    /// counter the loop steps (attempt++ &lt; 10, i &lt; 3) or a clock reading (sw.Elapsed &lt; limit)
    /// against a limit the loop doesn't reassign. !done, status != Ready, status.QueueDepth &gt; 0 and
    /// Ready() are readiness checks.
    /// </summary>
    private static bool IsBound(ExpressionSyntax condition, StatementSyntax loop) => condition switch
    {
        ParenthesizedExpressionSyntax p => IsBound(p.Expression, loop),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression) =>
            IsBound(b.Left, loop) && IsBound(b.Right, loop),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LessThanExpression) || b.IsKind(SyntaxKind.LessThanOrEqualExpression)
            || b.IsKind(SyntaxKind.GreaterThanExpression) || b.IsKind(SyntaxKind.GreaterThanOrEqualExpression) =>
            (IsCounterOrClock(Unwrap(b.Left), loop) && IsLimit(b.Right, loop)) || (IsCounterOrClock(Unwrap(b.Right), loop) && IsLimit(b.Left, loop)),
        _ => false,
    };

    private static readonly HashSet<string> ClockMembers =
        ["Elapsed", "ElapsedMilliseconds", "ElapsedTicks", "UtcNow", "Now", "TickCount", "TickCount64"];

    /// <summary>A break that leaves this loop, not an inner loop or a switch case.</summary>
    private static bool BreaksOut(SyntaxNode node, StatementSyntax loop) =>
        node is BreakStatementSyntax
        && node.Ancestors().FirstOrDefault(a => a is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
            or DoStatementSyntax or SwitchStatementSyntax) == loop;

    /// <summary>(x) and x! are x: neither changes what a comparison side reads.</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax p => Unwrap(p.Expression),
        PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.SuppressNullableWarningExpression) => Unwrap(post.Operand),
        _ => e,
    };

    private static bool IsCounterOrClock(ExpressionSyntax e, StatementSyntax loop)
    {
        if (e is PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax } or PrefixUnaryExpressionSyntax { Operand: IdentifierNameSyntax }
            && (e.IsKind(SyntaxKind.PostIncrementExpression) || e.IsKind(SyntaxKind.PostDecrementExpression)
                || e.IsKind(SyntaxKind.PreIncrementExpression) || e.IsKind(SyntaxKind.PreDecrementExpression)))
            return true;
        if (e is IdentifierNameSyntax id)
            return Steps(loop, id.Identifier.ValueText);
        // sw.Elapsed, DateTime.UtcNow - start: the clock, with no call that could read anything else.
        return e.DescendantNodesAndSelf().Any(n => n is MemberAccessExpressionSyntax m && ClockMembers.Contains(m.Name.Identifier.ValueText))
            && IsLimit(e, loop);
    }

    /// <summary>A value the loop doesn't change and that reads nothing live: no call (other than TimeSpan.From*) and no reassigned local.</summary>
    private static bool IsLimit(ExpressionSyntax e, StatementSyntax loop) =>
        !e.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax call && !call.Expression.ToString().StartsWith("TimeSpan.From", StringComparison.Ordinal))
        && !e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => Assigned(loop, i.Identifier.ValueText))
        && !e.DescendantNodesAndSelf().Any(n => n is MemberAccessExpressionSyntax m && !ClockMembers.Contains(m.Name.Identifier.ValueText)
            && m.Expression is IdentifierNameSyntax receiver && char.IsLower(receiver.Identifier.ValueText[0]));

    /// <summary>The loop steps a counter by ++, --, += or -= (in its body or a for's incrementors).</summary>
    private static bool Steps(StatementSyntax loop, string name) =>
        loop.DescendantNodes().Any(n => n switch
        {
            // x++ and x--, not x! (the null-forgiving operator is postfix too, and changes nothing)
            PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax o } post => o.Identifier.ValueText == name
                && (post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression)),
            PrefixUnaryExpressionSyntax { Operand: IdentifierNameSyntax o } pre => o.Identifier.ValueText == name
                && (pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression)),
            AssignmentExpressionSyntax { Left: IdentifierNameSyntax l } a => l.Identifier.ValueText == name
                && (a.IsKind(SyntaxKind.AddAssignmentExpression) || a.IsKind(SyntaxKind.SubtractAssignmentExpression)),
            _ => false,
        });

    /// <summary>
    /// The loop changes a local in any way (x = await ReadAsync(), expected += await CountAsync(),
    /// x++), so it can't be a fixed limit: comparing against it reads live state.
    /// </summary>
    private static bool Assigned(StatementSyntax loop, string name) =>
        Steps(loop, name)
        || loop.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Any(a => a.Left is IdentifierNameSyntax l && l.Identifier.ValueText == name);

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
