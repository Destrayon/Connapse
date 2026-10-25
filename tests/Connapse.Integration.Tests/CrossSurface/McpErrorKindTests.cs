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

    [Fact]
    public void ErrorKind_IgnoresWhatTheCallerSuppliedInQuotes()
    {
        string tools = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Mcp", "McpTools.cs"));
        string upload = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Services", "UploadService.cs"))
            + File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Application", "Files", "UploadOperations.cs"));
        var templates = ErrorLiteral().Matches(tools).Select(m => m.Groups["text"].Value)
            .Concat(ReturnedMessage().Matches(upload).Concat(ResultError().Matches(upload)).Select(m => "Error: " + m.Groups["text"].Value))
            .Where(t => QuotedHole().IsMatch(t))
            .Distinct()
            .ToList();
        const string hostile = "permission forbidden already exists not found invalid";

        templates.Should().NotBeEmpty();
        foreach (string template in templates)
        {
            string plain = Hole().Replace(template, "x");
            string loaded = Hole().Replace(QuotedHole().Replace(template, $"'{hostile}'"), "x");
            Surfaces.ErrorKind(loaded).Should().Be(Surfaces.ErrorKind(plain), template);
        }
    }

    /// <summary>
    /// The one message MCP passes through from a service, upload_file's "Error: {result.Error}",
    /// carries UploadService's messages, returned by its validation or built into an UploadResult,
    /// so those are read and checked too. A second
    /// pass-through fails the count, so its source gets the same coverage before it ships.
    /// </summary>
    [Fact]
    public void ErrorKind_MapsEveryUploadValidationMessageMcpPassesThrough()
    {
        string tools = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Mcp", "McpTools.cs"));
        PassThrough().Matches(tools).Select(m => m.Value).Should().Equal(["$\"Error: {attempt.Detail}\""]);

        // Refused(attempt) carries a container or file operation's detail: each must read as a kind.
        string containers = string.Concat(new[] { "Containers", "Files" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Connapse.Application", folder), "*.cs"))
            .Select(File.ReadAllText));
        var details = OperationDetail().Matches(containers).Select(m => "Error: " + Hole().Replace(m.Groups["text"].Value, "x")).ToList();
        details.Should().Contain("Error: You don't have permission to delete containers.")
            .And.Contain("Error: You don't have permission to delete files.");
        details.Where(t => Surfaces.ErrorKind(t) == "error:unknown").Should().BeEmpty();

        string upload = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Web", "Services", "UploadService.cs"))
            + File.ReadAllText(Path.Combine(RepoRoot(), "src", "Connapse.Application", "Files", "UploadOperations.cs"));
        var messages = ReturnedMessage().Matches(upload).Concat(ResultError().Matches(upload))
            .Select(m => "Error: " + Hole().Replace(m.Groups["text"].Value, "x"))
            .Distinct()
            .ToList();

        messages.Should().Contain("Error: Path traversal is not allowed.").And.Contain("Error: This container is not writable.");
        messages.Where(t => Surfaces.ErrorKind(t) == "error:unknown").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Uploaded 1 of 1 file(s) to container 'c'.", "ok")]
    [InlineData("Uploaded 0 of 1 file(s) to container 'c'.\n\nFailures:\n- a.exe: not supported", "error:all_failed failed=[a.exe: not supported]")]
    [InlineData("Deleted 1 of 2 file(s).\n\nFailures:\n- x: not found", "partial:1/2 failed=[x: not found]")]
    [InlineData("Deleted 2 of 2 file(s).\n\nWarnings (1):\n- d1: storage cleanup failed", "ok;storage_cleanup_failed=[d1]")]
    [InlineData("Deleted 2 of 2 file(s).\n\nWarnings (1):\n- d2: storage cleanup failed", "ok;storage_cleanup_failed=[d2]")]
    [InlineData("Deleted 2 of 2 file(s).\n\nWarnings (2):\n- d1: storage cleanup failed\n- d2: storage cleanup failed",
        "ok;storage_cleanup_failed=[d1,d2]")]
    [InlineData("File 'a.txt' (ID: d1) deleted from database, but the backing storage file could not be removed and may need manual cleanup.",
        "ok;storage_cleanup_failed=[d1]")]
    public void Result_ReadsBulkSummaries(string text, string expected)
    {
        var result = new ModelContextProtocol.Protocol.CallToolResult
        {
            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = text }],
        };

        Surfaces.Result(result).Should().Be(expected);
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

    /// <summary>An error built into the result rather than returned: new UploadResult(false, Error: "...").</summary>
    [GeneratedRegex(@"Error: \$?""(?<text>[^""]*)""")]
    private static partial Regex ResultError();

    /// <summary>A failure detail an operation writes: Fail(ContainerStatus.Forbidden, "..."), or a NameRule-style constant.</summary>
    [GeneratedRegex(@"(?:(?:Container|File)Status\.\w+, |const string \w+ = )\$?""(?<text>[^""]*)""")]
    private static partial Regex OperationDetail();

    [GeneratedRegex(@"'\{[^}]*\}'")]
    private static partial Regex QuotedHole();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Hole();
}
