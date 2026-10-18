using System.Text.RegularExpressions;
using FluentAssertions;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Every error message an MCP tool returns maps to a known kind, so a rejection MCP and REST share
/// compares equal instead of one side reading error:unknown. The messages are read from McpTools.cs,
/// so a new one fails here until it is mapped. error:unknown is left for unhandled exceptions.
/// </summary>
[Trait("Category", "Unit")]
public partial class McpErrorKindTests
{
    [Fact]
    public void ErrorKind_MapsEveryMcpToolErrorMessage()
    {
        string tools = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Mcp", "McpTools.cs"));
        var messages = ErrorLiteral().Matches(tools)
            .Select(m => Hole().Replace(m.Groups["text"].Value, "x"))
            .Where(t => t.Trim().Length > "Error:".Length && t.Trim() != "Error: x")
            .Distinct()
            .ToList();

        messages.Should().NotBeEmpty();
        messages.Where(t => Surfaces.ErrorKind(t) == "error:unknown").Should().BeEmpty();
    }

    /// <summary>
    /// The one message MCP passes through from a service, upload_file's "Error: {result.Error}",
    /// carries UploadService's validation messages, so those are read and checked too. A second
    /// pass-through fails the count, so its source gets the same coverage before it ships.
    /// </summary>
    [Fact]
    public void ErrorKind_MapsEveryUploadValidationMessageMcpPassesThrough()
    {
        string tools = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Mcp", "McpTools.cs"));
        PassThrough().Matches(tools).Select(m => m.Value).Should().Equal(["$\"Error: {result.Error}\""]);

        string upload = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Services", "UploadService.cs"));
        var messages = ReturnedMessage().Matches(upload)
            .Select(m => "Error: " + Hole().Replace(m.Groups["text"].Value, "x"))
            .Distinct()
            .ToList();

        messages.Should().Contain("Error: Path traversal is not allowed.");
        messages.Where(t => Surfaces.ErrorKind(t) == "error:unknown").Should().BeEmpty();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Connapse.slnx not found above the test output.");
    }

    [GeneratedRegex(@"\$?""(?<text>Error: [^""]*)""")]
    private static partial Regex ErrorLiteral();

    [GeneratedRegex(@"\$""Error: \{[^""]*""")]
    private static partial Regex PassThrough();

    [GeneratedRegex(@"return \$?""(?<text>[A-Z][^""]*)"";")]
    private static partial Regex ReturnedMessage();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Hole();
}
