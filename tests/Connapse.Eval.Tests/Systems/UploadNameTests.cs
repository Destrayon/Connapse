using Connapse.Core.Utilities;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;
using FluentAssertions;

namespace Connapse.Eval.Tests.Systems;

// #671: a titled text document is uploaded under its title, in a folder of its own.
[Trait("Category", "Unit")]
public class UploadNameTests
{
    private static EvalDocument Text(string? title) =>
        new("d1", DocumentKind.Text, title, "body", null, new Dictionary<string, string>());

    [Fact]
    public void UploadName_TitledText_IsTheTitleInItsOwnFolder()
    {
        EvalDocument doc = Text("Incident Review: API Gateway 5xx / autoscaler");

        ConnapseSearchSystem.UploadName(doc, 42).Should().Be("Incident Review API Gateway 5xx autoscaler.txt");
        ConnapseSearchSystem.UploadFolder(doc, 42).Should().Be("/0000042");
        PathUtilities.IsValidFileName(ConnapseSearchSystem.UploadName(doc, 42)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("::/..")]
    public void UploadName_NoUsableTitle_IsTheIndexAtTheRoot(string? title)
    {
        ConnapseSearchSystem.UploadName(Text(title), 42).Should().Be("0000042.txt");
        ConnapseSearchSystem.UploadFolder(Text(title), 42).Should().Be("/");
    }

    [Fact]
    public void SafeFileName_LongTitle_IsCappedAt200Characters()
    {
        ConnapseSearchSystem.SafeFileName(new string('a', 300))!.Length.Should().Be(200);
    }

    [Fact]
    public void SafeFileName_ControlCharacters_BecomeSpaces()
    {
        ConnapseSearchSystem.SafeFileName("line one\nline\ttwo").Should().Be("line one line two");
    }
}
