using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

[Trait("Category", "Unit")]
public class IngestionProbeTests
{
    [Fact]
    public void StripPageMarkers_RemovesOnlyMarkersForPagesTheParserMarked() =>
        IngestionProbe.StripPageMarkers("--- Page 1 --- intro. See --- Page 7 --- in the text.", new HashSet<int> { 1, 2 })
            .Should().Be(" intro. See --- Page 7 --- in the text.");

    [Fact]
    public void StripPageMarkers_NoMarkedPages_LeavesTheChunkAlone() =>
        IngestionProbe.StripPageMarkers("--- Page 1 --- literal", new HashSet<int>()).Should().Be("--- Page 1 --- literal");
}
