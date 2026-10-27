using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Eval.Runs;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class CodeEvalTests
{
    private static readonly CodeScope Scope = new(Guid.NewGuid(), null);

    private static CodeDefinition Definition(string path) => new("A.B", "method", "csharp", path, 1, 2, null, "d");

    private static CodeFileMatch File(string path) => new(path, "d", 1, "calls it", []);

    [Theory]
    [InlineData(5, 0.5)]   // two of a four-file key in the top five
    [InlineData(1, 1.0)]   // the denominator is capped at k
    public void Recall_IsHitsOverTheSmallerOfKeyAndK(int k, double expected) =>
        CodeEval.Recall(["a", "x", "b"], ["a", "b", "c", "d"], k).Should().Be(expected);

    [Fact]
    public async Task AskAsync_Definition_RanksTheDefinitionsFirstWithoutTheLeadingSlash()
    {
        ICodeTools code = Substitute.For<ICodeTools>();
        code.FindSymbolAsync(Scope, "Ledger.Post", CodeMatch.Exact, false, 20, Arg.Any<CancellationToken>())
            .Returns(new CodeSymbolResult([Definition("/src/Ledger.cs")], [], [File("/src/Other.cs"), File("/src/Ledger.cs")]));

        List<string> ranked = await CodeEval.AskAsync(code, Scope, Ask("definition"), default);

        ranked.Should().Equal("src/Ledger.cs", "src/Other.cs");
    }

    [Theory]
    [InlineData("callers", CodeReferenceKind.Calls)]
    [InlineData("usages", CodeReferenceKind.All)]
    public async Task AskAsync_References_RanksFilesThenTheDefinition(string kind, CodeReferenceKind expected)
    {
        ICodeTools code = Substitute.For<ICodeTools>();
        code.ReferencesAsync(Scope, "Ledger.Post", expected, 20, Arg.Any<CancellationToken>())
            .Returns(new CodeReferencesResult([Definition("/src/Ledger.cs")], [File("/src/Invoices.cs")]));

        List<string> ranked = await CodeEval.AskAsync(code, Scope, Ask(kind), default);

        ranked.Should().Equal("src/Invoices.cs", "src/Ledger.cs");
    }

    [Fact]
    public async Task AskAsync_How_FollowsTheDefinition()
    {
        ICodeTools code = Substitute.For<ICodeTools>();
        code.FindSymbolAsync(Scope, "Ledger.Post", CodeMatch.Exact, true, 20, Arg.Any<CancellationToken>())
            .Returns(new CodeSymbolResult([Definition("/src/Ledger.cs")], [], [File("/src/AuditLog.cs")]));

        List<string> ranked = await CodeEval.AskAsync(code, Scope, Ask("how"), default);

        ranked.Should().Equal("src/Ledger.cs", "src/AuditLog.cs");
    }

    private static CodeEval.Question Ask(string kind) => new("q", "repo", kind, "Ledger.Post", "tuning", ["src/Ledger.cs"]);
}
