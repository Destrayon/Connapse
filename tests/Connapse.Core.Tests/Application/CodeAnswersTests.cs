using Connapse.Application.Code;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-284: the code tools and the code routes read a match or kind, and shape an answer, with the same code, so this
/// is what both surfaces accept and show.
/// </summary>
[Trait("Category", "Unit")]
public class CodeAnswersTests
{
    private static readonly CodeDefinition Post = new(
        "Acme.Billing.Ledger.Post", "method", "csharp", "/src/Ledger.cs", 7, 12, "public void Post()", "doc-1");

    private static readonly CodeFileMatch Caller = new("/src/Invoices.cs", "doc-2", 2.91264, "calls it", [new CodeLine(9, "_ledger.Post();")]);

    private static readonly CodeUse Write = new("Acme.AuditLog.Write", "method", "/src/AuditLog.cs", 4, "calls", 0.9);

    [Theory]
    [InlineData(null, CodeMatch.Exact)]
    [InlineData("", CodeMatch.Exact)]
    [InlineData("  ", CodeMatch.Exact)]
    [InlineData("EXACT", CodeMatch.Exact)]
    [InlineData(" Words ", CodeMatch.Words)]
    [InlineData("meaning", CodeMatch.Meaning)]
    public void TryMatch_KnownMatch_ReadsIt(string? match, CodeMatch expected)
    {
        CodeAnswers.TryMatch(match, out CodeMatch mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("fuzzy")]
    [InlineData("2")]
    public void TryMatch_UnknownMatch_IsRefused(string match) =>
        CodeAnswers.TryMatch(match, out _).Should().BeFalse();

    [Theory]
    [InlineData(null, CodeReferenceKind.All)]
    [InlineData("", CodeReferenceKind.All)]
    [InlineData("All", CodeReferenceKind.All)]
    [InlineData("calls", CodeReferenceKind.Calls)]
    [InlineData(" CALLERS ", CodeReferenceKind.Calls)]
    [InlineData("inherits", CodeReferenceKind.Inherits)]
    public void TryKind_KnownKind_ReadsIt(string? kind, CodeReferenceKind expected)
    {
        CodeAnswers.TryKind(kind, out CodeReferenceKind which).Should().BeTrue();
        which.Should().Be(expected);
    }

    [Theory]
    [InlineData("subtypes")]
    [InlineData("1")]
    public void TryKind_UnknownKind_IsRefused(string kind) =>
        CodeAnswers.TryKind(kind, out _).Should().BeFalse();

    [Fact]
    public void Shown_Definition_DropsTheLeadingSlashAndKeepsTheRest() =>
        CodeAnswers.Shown(Post).Should().Be(new CodeDefinitionShown(
            "Acme.Billing.Ledger.Post", "method", "csharp", "src/Ledger.cs", 7, 12, "public void Post()", "doc-1"));

    [Fact]
    public void Shown_File_DropsTheLeadingSlashAndRoundsTheScoreToThreePlaces()
    {
        CodeFileShown shown = CodeAnswers.Shown(Caller);

        (shown.Path, shown.DocumentId, shown.Score, shown.Reason).Should().Be(("src/Invoices.cs", "doc-2", 2.913, "calls it"));
        shown.Lines.Should().Equal(Caller.Lines);
    }

    [Fact]
    public void Symbol_Followed_ShowsUses_AndUnfollowed_LeavesThemOut()
    {
        CodeSymbolResult result = new([Post], [Write], [Caller]);

        CodeSymbolAnswer followed = CodeAnswers.Symbol(result, follow: true);
        followed.Notice.Should().Be(CodeAnswers.Notice);
        followed.Definitions.Should().Equal(CodeAnswers.Shown(Post));
        followed.Uses.Should().Equal(Write);
        followed.Files.Should().Equal(CodeAnswers.Shown(Caller));

        CodeAnswers.Symbol(result, follow: false).Uses.Should().BeNull();
    }

    [Fact]
    public void References_ShowsDefinitionsAndFiles()
    {
        CodeReferencesAnswer answer = CodeAnswers.References(new CodeReferencesResult([Post], [Caller]));

        answer.Notice.Should().Be(CodeAnswers.Notice);
        answer.Definitions.Should().Equal(CodeAnswers.Shown(Post));
        answer.Files.Should().Equal(CodeAnswers.Shown(Caller));
    }

    [Fact]
    public void Impact_ShowsAffectedSymbolsFilesAndTests()
    {
        CodeImpacted close = new("Acme.Billing.Invoices.Close", "method", "/src/Invoices.cs", 1, 1, IsTest: false);

        CodeImpactAnswer answer = CodeAnswers.Impact(new CodeImpactResult([Post], [close], [Caller], ["/tests/LedgerTests.cs"]));

        answer.Notice.Should().Be(CodeAnswers.Notice);
        answer.Definitions.Should().Equal(CodeAnswers.Shown(Post));
        answer.Affected.Should().Equal(close);
        answer.Files.Should().Equal(CodeAnswers.Shown(Caller));
        answer.Tests.Should().Equal("/tests/LedgerTests.cs");
    }

    [Fact]
    public void Map_ShowsTheTotalAndTheRankedFiles()
    {
        CodeMapFile ledger = new("/src/Ledger.cs", "doc-1", 0.42, [new CodeMapSymbol("Acme.Billing.Ledger", "class", 5, null, 3)]);

        CodeMapAnswer answer = CodeAnswers.Map(new CodeRepoMap([ledger], 7));

        answer.Should().BeEquivalentTo(new CodeMapAnswer(CodeAnswers.Notice, 7, [ledger]));
    }
}
