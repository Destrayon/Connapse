using CsCheck;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

// Which of the facts a save's search found the save shows (CON-332). Words are given as Postgres's english stemmer gives them.
[Trait("Category", "Unit")]
public class MemoryFactMatcherTests
{
    private static readonly Guid Space = Guid.NewGuid();
    private static readonly MemoryCaller Nobody = new(null, null, null);

    [Fact]
    public void Match_TheSameSubjectAndAttribute_ComesFirst()
    {
        MemoryMatchCandidate status = Candidate("CON-291", "status", "In review", W("con 291", "statu", "review"));
        MemoryMatchCandidate reworded = Candidate("CON-291", "current status", "In review", W("con 291", "current statu", "review"));

        IReadOnlyList<MemoryFactMatch> matches = Match(("con-291", "Status", "Merged"), W("con 291", "statu", "merg"), reworded, status);

        matches.Select(m => m.Fact).Should().Equal(status.Fact, reworded.Fact);
        matches[0].Kind.Should().Be(MemoryMatchKind.SameSubjectAndAttribute);
        matches[1].Kind.Should().Be(MemoryMatchKind.SameSubject);
    }

    [Theory]
    [InlineData("state", "state", "status", "statu")]
    [InlineData("deployment target", "deploy target", "deploy target", "deploy target")]
    [InlineData("oncall", "oncal", "on-call rotation", "call rotat")]
    [InlineData("session timeout", "session timeout", "session length", "session length")]
    public void Match_AnAttributeReworded_ShowsTheFact(string mine, string myWords, string theirs, string theirWords)
    {
        MemoryMatchCandidate old = Candidate("Wicket", theirs, "8 hours", W("wicket", theirWords, "8 hour"));

        Match(("Wicket", mine, "12 hours"), W("wicket", myWords, "12 hour"), old).Should().ContainSingle()
            .Which.Kind.Should().Be(MemoryMatchKind.SameSubject);
    }

    [Fact]
    public void Match_AnotherAttributeWithTheSameValueWords_ShowsTheFact()
    {
        MemoryMatchCandidate old = Candidate("Halcyon", "database", "PostgreSQL 15", W("halcyon", "databas", "postgresql 15"));

        Match(("Halcyon", "db engine", "Postgres 15"), W("halcyon", "db engin", "postgr 15"), old).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Halcyon API", "halcyon api")]
    [InlineData("Halycon", "halycon")]
    [InlineData("Project Halcyon", "project halcyon")]
    public void Match_ASubjectSpelledOrExtendedDifferently_ShowsTheFact(string subject, string words)
    {
        MemoryMatchCandidate old = Candidate("Halcyon", "owner", "Ravi Banerjee", W("halcyon", "owner", "ravi banerje"));

        Match((subject, "owner", "Mei Lin"), W(words, "owner", "mei lin"), old).Should().ContainSingle()
            .Which.Kind.Should().Be(MemoryMatchKind.SimilarSubject);
    }

    [Fact]
    public void Match_ASubjectPunctuatedDifferently_IsTheSameSubject()
    {
        MemoryMatchCandidate old = Candidate("Ferro scheduler", "runtime", "dotnet 8", W("ferro schedul", "runtim", "dotnet 8"));

        Match(("ferro-scheduler", "Runtime", "dotnet 10"), W("ferro schedul", "runtim", "dotnet 10"), old).Should().ContainSingle()
            .Which.Kind.Should().Be(MemoryMatchKind.SameSubjectAndAttribute);
    }

    [Theory]
    [InlineData("CON-292", "con 292", "CON-291", "con 291")]
    [InlineData("Release 0.5.1", "releas 0 5 1", "Release 0.5.0", "releas 0 5 0")]
    public void Match_ASubjectWithOtherNumbers_ShowsNothing(string mine, string myWords, string theirs, string theirWords)
    {
        MemoryMatchCandidate old = Candidate(theirs, "status", "In review", W(theirWords, "statu", "review"));

        Match((mine, "status", "Merged"), W(myWords, "statu", "merg"), old).Should().BeEmpty();
    }

    [Fact]
    public void Match_TheSameSubjectWithAnUnrelatedAttributeAndValue_ShowsNothing()
    {
        MemoryMatchCandidate old = Candidate("Halcyon", "database", "PostgreSQL 15", W("halcyon", "databas", "postgresql 15"));

        Match(("Halcyon", "owner", "Ravi Banerjee"), W("halcyon", "owner", "ravi banerje"), old).Should().BeEmpty();
    }

    [Fact]
    public void Match_AnotherSubjectWithTheSameAttribute_ShowsNothing()
    {
        MemoryMatchCandidate old = Candidate("Halcyon", "database", "PostgreSQL 15", W("halcyon", "databas", "postgresql 15"));

        Match(("Gannet", "database", "MySQL 8"), W("gannet", "databas", "mysql 8"), old).Should().BeEmpty();
    }

    [Fact]
    public void Match_ASubjectAnotherFactDescribesInItsWords_ShowsThatSubjectsFact()
    {
        MemoryMatchCandidate kind = Candidate("Wicket", "kind", "SSO gateway", W("wicket", "kind", "sso gateway"));
        MemoryMatchCandidate owner = Candidate("Wicket", "owner", "Dana Whitlock", W("wicket", "owner", "dana whitlock"));

        Match(("SSO gateway", "owner", "Ravi Banerjee"), W("sso gateway", "owner", "ravi banerje"), kind, owner)
            .Should().ContainSingle().Which.Fact.Should().Be(owner.Fact);
    }

    [Fact]
    public void Match_ItsOwnRowOrChain_IsNeverShown()
    {
        MemoryMatchCandidate self = Candidate("CON-291", "status", "Merged", W("con 291", "statu", "merg"));

        MemoryFactMatcher.Match(self.Fact, self.Words, [self]).Should().BeEmpty();
    }

    [Fact]
    public void Match_ManyClose_ShowsAtMostThreeClosestFirst()
    {
        MemoryMatchCandidate[] candidates = [.. Enumerable.Range(0, 6)
            .Select(i => Candidate("CON-291", i == 4 ? "status" : $"status {i}", "x", W("con 291", "statu", "x")))];

        IReadOnlyList<MemoryFactMatch> matches = Match(("CON-291", "status", "Merged"), W("con 291", "statu", "merg"), candidates);

        matches.Should().HaveCount(MemoryFactMatcher.MaxMatches);
        matches[0].Fact.Should().Be(candidates[4].Fact);
    }

    [Fact]
    public void Match_AnyFactsAndWords_ShowsAtMostThreeOfTheCandidatesWithoutThrowing()
    {
        Gen<string> text = Gen.String[Gen.Char["aAbc-09 .é"], 0, 12];
        Gen<string[]> words = Gen.String[Gen.Char["abc019"], 0, 6].Array[0, 4];
        Gen<(string S, string A, string V, string[] Ws, string[] Wa, string[] Wv)> fact = Gen.Select(text, text, text, words, words, words);

        Gen.Select(fact, fact.Array[0, 8]).Sample(t =>
        {
            (var saved, var others) = t;
            MemoryMatchCandidate mine = Candidate(saved.S, saved.A, saved.V, new(saved.Ws, saved.Wa, saved.Wv));
            MemoryMatchCandidate[] candidates = [.. others.Select(o => Candidate(o.S, o.A, o.V, new(o.Ws, o.Wa, o.Wv)))];

            IReadOnlyList<MemoryFactMatch> first = MemoryFactMatcher.Match(mine.Fact, mine.Words, candidates);
            IReadOnlyList<MemoryFactMatch> again = MemoryFactMatcher.Match(mine.Fact, mine.Words, [.. candidates.Reverse()]);

            first.Count.Should().BeLessThanOrEqualTo(MemoryFactMatcher.MaxMatches);
            first.Select(m => m.Fact).Should().OnlyContain(f => candidates.Any(c => c.Fact == f)).And.OnlyHaveUniqueItems();
            first.Select(m => m.Kind).Should().BeInAscendingOrder();
            again.Select(m => m.Fact.Id).Should().Equal(first.Select(m => m.Fact.Id), "the order found must not change what is shown");
        });
    }

    private static IReadOnlyList<MemoryFactMatch> Match(
        (string Subject, string Attribute, string Value) saved, MemoryMatchWords words, params MemoryMatchCandidate[] candidates) =>
        MemoryFactMatcher.Match(Fact(saved.Subject, saved.Attribute, saved.Value), words, candidates);

    private static MemoryMatchWords W(string subject, string attribute, string value) =>
        new(Split(subject), Split(attribute), Split(value));

    private static string[] Split(string words) => words.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static MemoryMatchCandidate Candidate(string subject, string attribute, string value, MemoryMatchWords words) =>
        new(Fact(subject, attribute, value), words, 0.5f);

    private static MemoryFact Fact(string subject, string attribute, string value)
    {
        Guid id = Guid.NewGuid();
        return new MemoryFact(id, id, Space, subject, attribute, value, null, null, DateTimeOffset.UnixEpoch, null, null, null,
            Nobody, [], MemoryScope.Team);
    }
}
