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
        scopes.UriPrefixes.Should().HaveCount(2);
    }

    [Fact]
    public void Of_WithNothingUsable_IsNoneRatherThanUnrestricted()
    {
        // A resolver that returns an empty or blank-filled list means the user has no grants. The
        // dangerous reading is "no restrictions", and this is where that reading is refused.
        SearchScopes.Of([]).Should().BeSameAs(SearchScopes.None);
        SearchScopes.Of(["", "   "]).Should().BeSameAs(SearchScopes.None);
    }

    [Fact]
    public void Of_DropsBlankEntriesBetweenRealOnes()
    {
        // A blank prefix would match every URI, so one stray empty string in a resolver's output
        // would silently grant everything to a user who should see one bucket.
        SearchScopes.Of(["s3://acme/team/", "", "s3://acme/shared/"])
            .UriPrefixes.Should().BeEquivalentTo(["s3://acme/team/", "s3://acme/shared/"]);
    }

    [Fact]
    public void Of_WithNull_Throws()
    {
        FluentActions.Invoking(() => SearchScopes.Of(null!))
            .Should().Throw<ArgumentNullException>();
    }
}
