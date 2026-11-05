using Connapse.Core;
using Connapse.Web.Components.Memory;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>CON-249: the words the grouped memory list shows for a subject and for a page of subjects.</summary>
[Trait("Category", "Unit")]
public class MemorySubjectGroupTests
{
    private static RecalledFact Fact(string subject)
    {
        Guid id = Guid.NewGuid();
        return new RecalledFact(new MemoryFact(id, id, Guid.NewGuid(), subject, "a", "v", null, null, DateTimeOffset.UtcNow,
            null, null, null, new MemoryCaller(null, null, "pat"), []), []);
    }

    [Fact]
    public void Add_LastPage_MakesTheFactsLoadedTheCount()
    {
        MemorySubjectGroup group = new(new MemorySubject("Halcyon", 9), open: true, total: null);

        group.Add([Fact("Halcyon"), Fact("Halcyon")], next: null);

        group.CountLabel.Should().Be("2 facts");
        group.Loaded.Should().BeTrue();
    }

    [Fact]
    public void Labels_UnknownTotal_SayAtLeastAndShowMoreWithoutANumber()
    {
        MemorySubjectGroup group = new(new MemorySubject("Halcyon", 9), open: true, total: null);

        group.Add([Fact("Halcyon"), Fact("Halcyon"), Fact("Halcyon")], next: "cursor");

        group.CountLabel.Should().Be("3+ facts");
        group.MoreLabel.Should().Be("Show more in Halcyon");
    }

    [Fact]
    public void Labels_KnownTotal_CountWhatIsLeft()
    {
        MemorySubjectGroup group = new(new MemorySubject("Halcyon", 1200), open: true, total: 1200);

        group.Add([Fact("Halcyon"), Fact("Halcyon"), Fact("Halcyon")], next: "cursor");

        group.CountLabel.Should().Be("1,200 facts");
        group.MoreLabel.Should().Be("Show 1,197 more in Halcyon");
    }

    [Theory]
    [InlineData(1, 1, false, null, "All 1 subject")]
    [InlineData(50, 2, true, null, "50 subjects on page 2")]
    [InlineData(2, 1, false, "refund", "All 2 subjects matching “refund”")]
    public void Range_SaysWhichSubjectsAreShown(int subjects, int page, bool more, string? text, string expected) =>
        MemorySubjectGroup.Range(subjects, page, more, text, null).Should().Be(expected);

    [Fact]
    public void Range_WithACappedCount_SaysAtLeast() =>
        MemorySubjectGroup.Range(3, 1, false, null, new MemoryFactCount(10_000, Capped: true)).Should().Be("All 3 subjects · 10,000+ facts");
}
