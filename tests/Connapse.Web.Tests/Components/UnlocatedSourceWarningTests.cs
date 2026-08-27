using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// What the operator is told about documents with no recorded coordinate.
/// </summary>
/// <remarks>
/// The wording matters more than the markup. "Re-sync to record where these came from" tells an
/// operator what to do; "some documents are missing metadata" does not, and the consequence of not
/// acting -- an entire corpus vanishing when filtering is enabled -- is invisible from the second.
/// </remarks>
[Trait("Category", "Unit")]
public class UnlocatedSourceWarningTests
{
    private static readonly string Markup = ReadMarkup();

    private static string ReadMarkup()
    {
        string path = Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Sources.razor");

        string content = File.ReadAllText(path);

        // File.ReadAllText silently returns an empty string for an empty file rather than
        // throwing, and every assertion below is a Contain check that would fail with a
        // misleading "expected string to contain X" instead of the real problem: the file
        // could not be read. Fail loudly with the actual cause instead.
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                $"Sources.razor at '{path}' was empty or could not be read.");
        }

        return content;
    }

    [Fact]
    public void SourcesPage_NamesTheActionThatFixesIt()
    {
        Markup.Should().Contain("Re-sync",
            "an operator needs to be told what to do, not only that something is wrong");
    }

    [Fact]
    public void SourcesPage_ExplainsWhyItMatters()
    {
        Markup.Should().Contain("per-user permissions",
            "the consequence is invisible unless the warning says what breaks");
    }

    [Fact]
    public void SourcesPage_ReadsTheReport()
    {
        Markup.Should().Contain("UnlocatedBySourceAsync",
            "the warning must come from the report rather than a guess");
    }
}
