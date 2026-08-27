using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// What a search may reach, and the one distinction the whole filter rests on.
/// </summary>
[Trait("Category", "Unit")]
public class SearchScopesTests
{
    [Fact]
    public void Unrestricted_AndNone_AreOpposites()
    {
        // The distinction that must never collapse. "This deployment does not filter" and "this
        // user reaches nothing" would both be an empty list in a naive model, and confusing them
        // turns a misconfiguration into an open door rather than a closed one.
        SearchScopes.Unrestricted.IsUnrestricted.Should().BeTrue();
        SearchScopes.Unrestricted.IsEmpty.Should().BeFalse();

        SearchScopes.None.IsUnrestricted.Should().BeFalse();
        SearchScopes.None.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Of_WithPrefixes_RestrictsToThem()
    {
        var scopes = SearchScopes.Of(["s3://acme/team/", "s3://acme/shared/"]);

        scopes.IsUnrestricted.Should().BeFalse();
        scopes.IsEmpty.Should().BeFalse();
        scopes.Matches.Should().HaveCount(2);
    }

    [Fact]
    public void Of_WithNothingUsable_IsNoneRatherThanUnrestricted()
    {
        // A resolver that returns an empty or blank-filled list means the user has no grants. The
        // dangerous reading is "no restrictions", and this is where that reading is refused.
        SearchScopes.Of(Array.Empty<string>()).Should().BeSameAs(SearchScopes.None);
        SearchScopes.Of(["", "   "]).Should().BeSameAs(SearchScopes.None);
    }

    [Fact]
    public void Of_DropsBlankEntriesBetweenRealOnes()
    {
        // A blank prefix would match every URI, so one stray empty string in a resolver's output
        // would silently grant everything to a user who should see one bucket.
        SearchScopes.Of(["s3://acme/team/", "", "s3://acme/shared/"])
            .Matches.Select(m => m.Value)
            .Should().BeEquivalentTo(["s3://acme/team/", "s3://acme/shared/"]);
    }

    [Fact]
    public void Of_WithNull_Throws()
    {
        FluentActions.Invoking(() => SearchScopes.Of((IReadOnlyList<string>)null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Of_WithMatches_KeepsExactAndPrefixApart()
    {
        // The two kinds cannot be collapsed: an exact match is one object, a prefix is a subtree.
        var scopes = SearchScopes.Of([
            new GrantMatch("s3://acme/team/", IsExact: false),
            new GrantMatch("s3://acme/reports/q3.pdf", IsExact: true),
        ]);

        scopes.Matches.Should().HaveCount(2);
        scopes.Matches.Should().ContainSingle(m => m.IsExact);
    }

    [Fact]
    public void Of_WithStrings_TreatsEachAsAPrefix()
    {
        SearchScopes.Of(["s3://acme/team/"]).Matches
            .Should().BeEquivalentTo([new GrantMatch("s3://acme/team/", IsExact: false)]);
    }

    // -- LIKE escaping -------------------------------------------------------------

    [Fact]
    public void ToLikePattern_EscapesUnderscore_SoAGrantIsNotAWildcard()
    {
        // The bypass this closes. "_" matches any single character, so a grant for
        // s3://acme/team_docs/ also matched s3://acme/teamXdocs/ -- a prefix nobody granted, and
        // underscores in S3 key prefixes are ordinary rather than exotic.
        SearchScopes.ToLikePattern("s3://acme/team_docs/")
            .Should().Be("s3://acme/team!_docs/%");
    }

    [Fact]
    public void ToLikePattern_EscapesPercent()
    {
        // Worse than the underscore: "%" matches any sequence, so one in a prefix widens the grant
        // to everything sharing whatever came before it.
        SearchScopes.ToLikePattern("s3://acme/50%off/")
            .Should().Be("s3://acme/50!%off/%");
    }

    [Fact]
    public void ToLikePattern_EscapesTheEscapeCharacterItself()
    {
        // And does it first. Escaping the wildcards before the escape character would re-escape
        // this method's own output, turning a literal "!" into an escape and shifting everything
        // after it by one.
        SearchScopes.ToLikePattern("s3://acme/hey!/")
            .Should().Be("s3://acme/hey!!/%");
    }

    [Fact]
    public void ToLikePattern_LeavesAnOrdinaryPrefixAlone()
    {
        SearchScopes.ToLikePattern("s3://acme/team/").Should().Be("s3://acme/team/%");
    }

    [Fact]
    public void ToLikePattern_WithNull_Throws()
    {
        FluentActions.Invoking(() => SearchScopes.ToLikePattern(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
