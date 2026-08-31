using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// Reading the group block back, so Connapse can name the group in a grant command.
/// </summary>
[Trait("Category", "Unit")]
public class DirectoryGroupParseTests
{
    private const string Id = "69f9f9de-00f1-7088-80ba-6fe7914cb986";

    private static string Block(string id = Id, string name = "Connapse Readers") =>
        $"""
        {DirectoryGroupSetup.BeginMarker}
        groupId={id}
        groupName={name}
        {DirectoryGroupSetup.EndMarker}
        """;

    [Fact]
    public void ParseResult_ReadsTheIdAndName()
    {
        var result = DirectoryGroupSetup.ParseResult(Block());

        result.Should().NotBeNull();
        result!.Value.Id.Should().Be(Id);
        result.Value.Name.Should().Be("Connapse Readers");
    }

    [Fact]
    public void ParseResult_AnchorsOnTheLastMarkerPair()
    {
        // A terminal buffer holds the markers twice: the echoed script contains them, because
        // printing them is its job. Taking the first pair reads the source, whose body is printf
        // lines that parse to no fields at all.
        string pasted = DirectoryGroupSetup.GenerateScript("us-west-1", "d-1234567890", null, "X")
                        + "\n" + Block();

        DirectoryGroupSetup.ParseResult(pasted)!.Value.Id.Should().Be(Id);
    }

    [Fact]
    public void ParseResult_WithoutAnId_ReturnsNull()
    {
        // A name with no id records nothing a command could reference, and would show a group in
        // the UI that no grant can name.
        string pasted = $"""
            {DirectoryGroupSetup.BeginMarker}
            groupName=Connapse Readers
            {DirectoryGroupSetup.EndMarker}
            """;

        DirectoryGroupSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void ParseResult_WithAnIdThatCouldNotBeOne_ReturnsNull()
    {
        // The id is interpolated into a command an administrator runs. It goes through the same
        // allowlist as everything else that reaches a shell.
        DirectoryGroupSetup.ParseResult(Block(id: "$(id); rm -rf /")).Should().BeNull();
    }

    [Fact]
    public void ParseResult_WithAnUnusableName_KeepsTheIdAndDropsTheName()
    {
        // The id is what a grant needs. A name that cannot be shown safely is not a reason to
        // discard a working group.
        var result = DirectoryGroupSetup.ParseResult(Block(name: "back`tick`"));

        result.Should().NotBeNull();
        result!.Value.Id.Should().Be(Id);
        result.Value.Name.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated terminal output")]
    public void ParseResult_WithNothingUsable_ReturnsNull(string? pasted)
    {
        DirectoryGroupSetup.ParseResult(pasted).Should().BeNull();
    }

    [Fact]
    public void GenerateScript_PrintsTheBlockFromEitherBranch()
    {
        // Choosing a group that already exists must record it just as creating one does, or an
        // administrator whose groups come from Okta could never store one.
        string script = DirectoryGroupSetup.GenerateScript("us-west-1", "d-1234567890", "u-1", "X");

        script.Should().Contain(DirectoryGroupSetup.BeginMarker);
        script.Should().Contain("groupId=%s");
        script.Should().Contain("[ -n \"$GROUP_ID\" ]",
            "the block is printed whenever a group was found or made, not only when one was made");
    }
}
