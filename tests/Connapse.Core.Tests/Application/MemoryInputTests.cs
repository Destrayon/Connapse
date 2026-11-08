using Connapse.Application.Memory;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// CON-282: the API and the memory tools read a fact's dates, sources and correction kind through one parser, so the
/// same text means the same fact on both.
/// </summary>
[Trait("Category", "Unit")]
public class MemoryInputTests
{
    [Fact]
    public void Dates_BareDateIsMidnightUtc_OffsetIsKeptAsUtc_AndBlankIsNull()
    {
        MemoryInput.Dates("2026-03-01", "  ").Should().Be(((DateTimeOffset?)new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), (DateTimeOffset?)null));
        MemoryInput.Dates(null, "2026-03-01T10:00:00+02:00")!.Value.To.Should().Be(new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero));
        MemoryInput.Dates(null, "2026-03-01T10:00:00+02:00")!.Value.To!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("last tuesday", null)]
    [InlineData(null, "13/45/2026")]
    public void Dates_EitherNotADate_IsNull(string? from, string? to) => MemoryInput.Dates(from, to).Should().BeNull();

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" 2026-03-01 ", true)]
    [InlineData("soon", false)]
    public void TryDate_BlankIsNoDate_TextIsTrimmed_AndNonsenseFails(string? text, bool ok)
    {
        MemoryInput.TryDate(text, out DateTimeOffset? value).Should().Be(ok);
        if (ok && !string.IsNullOrWhiteSpace(text))
            value.Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        else
            value.Should().BeNull();
    }

    [Fact]
    public void Sources_UuidIsADocument_NodePrefixIsANode_AndNoneIsEmpty()
    {
        Guid document = Guid.NewGuid();
        MemoryInput.Sources([$" {document.ToString().ToUpperInvariant()} ", "NODE:HAL-17"]).Should().Equal(
            new MemorySource(MemorySourceKind.Document, document.ToString()), new MemorySource(MemorySourceKind.Node, "HAL-17"));
        MemoryInput.Sources(null).Should().BeEmpty();
        MemoryInput.Sources([]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("HAL-17")]
    [InlineData("node:")]
    [InlineData("")]
    [InlineData(null)]
    public void Sources_AnyOneNeitherADocumentNorANode_FailsTheWholeList(string? bad) =>
        MemoryInput.Sources([Guid.NewGuid().ToString(), bad]).Should().BeNull();

    [Theory]
    [InlineData(null, MemoryCorrection.Changed)]
    [InlineData("", MemoryCorrection.Changed)]
    [InlineData(" Changed ", MemoryCorrection.Changed)]
    [InlineData("WRONG", MemoryCorrection.Wrong)]
    [InlineData("1", null)]
    [InlineData("superseded", null)]
    public void Correction_ChangedByDefault_WrongByName_AnythingElseIsNull(string? kind, MemoryCorrection? expected) =>
        MemoryInput.Correction(kind).Should().Be(expected);
}
