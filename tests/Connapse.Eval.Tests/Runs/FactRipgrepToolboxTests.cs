using System.Diagnostics;
using System.Text.Json;
using Connapse.Core;
using Connapse.Eval.Runs;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class FactRipgrepToolboxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Space = Guid.NewGuid();

    private static MemoryFact Fact(string subject, string attribute, string value, int order) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Space, subject, attribute, value, null, null, Now.AddMinutes(order),
            null, null, null, new MemoryCaller(null, null, null), []);

    private static Dictionary<string, JsonElement> Input(object args) =>
        JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> Input(params (string Key, object Value)[] args) =>
        args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value), StringComparer.Ordinal);

    /// <summary>The tests that run rg itself are skipped where it is not installed, as on some CI runners.</summary>
    private static bool HasRipgrep()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(FactRipgrepToolbox.Binary, "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void Arguments_MapClaudeCodeGrepOptionsToRipgrepFlags()
    {
        (List<string>? args, string? error) = FactRipgrepToolbox.Arguments(Input(("pattern", "Post"), ("-i", true), ("-C", 1), ("-o", true)));

        error.Should().BeNull();
        args.Should().ContainInOrder("--no-config", "--line-number", "--ignore-case", "--only-matching", "--context=1", "--regexp", "Post");
    }

    [Fact]
    public void Arguments_CountModeDropsLineNumbersAndContext()
    {
        (List<string>? args, _) = FactRipgrepToolbox.Arguments(Input(("pattern", "x"), ("output_mode", "count"), ("-A", 2)));

        args.Should().Contain("--count").And.NotContain("--line-number").And.NotContain("--after-context=2");
    }

    [Fact]
    public void Arguments_MissingPatternOrNegativeContext_IsAnError()
    {
        FactRipgrepToolbox.Arguments(Input(new { })).Error.Should().StartWith("Error: Missing");
        FactRipgrepToolbox.Arguments(Input(("pattern", "x"), ("-B", -1))).Error.Should().StartWith("Error:");
    }

    [Fact]
    public async Task Search_RunsRipgrep_NumbersLines_AndHonorsHeadLimit()
    {
        if (!HasRipgrep())
            return;
        List<string> lines = [FactGrepToolbox.Header, "a | Harrow | db | PostgreSQL | | current", "b | Lantern | db | Redis | | current", "c | Quill | db | Postgres | | current"];

        (await FactRipgrepToolbox.SearchAsync(lines, Input(new { pattern = "harrow" }), CancellationToken.None)).Should().Be("No lines match.");
        (await FactRipgrepToolbox.SearchAsync(lines, Input(("pattern", "harrow"), ("-i", true)), CancellationToken.None))
            .Should().Be("2:a | Harrow | db | PostgreSQL | | current");
        (await FactRipgrepToolbox.SearchAsync(lines, Input(("pattern", "Postgre"), ("head_limit", 1)), CancellationToken.None))
            .Should().Be("2:a | Harrow | db | PostgreSQL | | current\n(1 more output lines not shown; raise head_limit or use offset)");
        (await FactRipgrepToolbox.SearchAsync(lines, Input(("pattern", "Postgre"), ("output_mode", "count")), CancellationToken.None)).Should().Be("2");
        (await FactRipgrepToolbox.SearchAsync(lines, Input(new { pattern = "(" }), CancellationToken.None)).Should().StartWith("Error:");
    }

    [Fact]
    public async Task CallAsync_OffersRgAndRead_AndScoresRgAsARecall()
    {
        if (!HasRipgrep())
            return;
        MemoryFact fact = Fact("Harrow", "release branch", "release/2.4", 0);
        FactRipgrepToolbox tools = new(_ => Task.FromResult<IReadOnlyList<MemoryFact>>([fact]));
        tools.Tools.Select(t => t.Name).Should().Equal("rg", "read");
        string output = await tools.CallAsync("rg", Input(new { pattern = "release" }), CancellationToken.None);
        output.Should().StartWith($"2:{fact.Id} | Harrow");

        MemoryAgentEval.Seeded seed = new(Space, new Dictionary<string, Guid> { ["f001"] = fact.Id }, new Dictionary<string, Guid>(),
            new Dictionary<string, Guid> { ["f001"] = fact.ChainId });
        AgentRun run = new(AgentOutcome.Answered, "release/2.4", [fact.Id.ToString()],
            [new ToolTrace("rg", Input(new { pattern = "release" }), output)], new TokenUsage(10, 5, 0, 0, 0.01), 2, null);
        MemoryAgentEval.TaskScore score = MemoryAgentEval.ScoreFind(
            new MemoryAgentEval.AgentTask("find-x", "find", "reworded", "test", "q", ["f001"], null, []), run, seed);

        score.Score.Should().Be(1);
        score.Measures["firstRecallHit"].Should().Be(1);
        score.Measures["recallCalls"].Should().Be(1);
    }

    [Fact]
    public void Prompts_RipgrepArmSaysRgAndNamesTheSpaceForSaves()
    {
        MemoryAgentEval.FindPrompt("Quorvane Logistics", "ripgrep").Should().Contain("search with rg (ripgrep) and read");
        MemoryAgentEval.SavePrompt("Quorvane Logistics", "ripgrep").Should().Contain("search with rg (ripgrep)")
            .And.Contain("memory_remember (space \"pm-desk\")");
    }
}
