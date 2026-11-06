using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Eval.Runs.MemorySearch;
using FluentAssertions;

namespace Connapse.Eval.Tests.Runs;

[Trait("Category", "Unit")]
public class MemoryGuardTests
{
    private const int FailedToStart = -1073741502;
    private const string Allowed = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"seven_day":{"utilization":0.31}}}}""";
    private const string Finished = """{"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn","num_turns":1,"result":"done","modelUsage":{}}""";
    private const string Init = """{"type":"system","subtype":"init","tools":["mcp__connapse__answer"]}""";
    private static readonly DocumentIndex Index = new(
        new Dictionary<string, string> { ["11111111-1111-1111-1111-111111111111"] = "doc-1" }, new Dictionary<string, string>());

    private sealed class NoTools : IToolbox
    {
        public IReadOnlyList<ToolSpec> Tools => [];

        public Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class ScriptedRunner(params ProcessResult[] script) : IProcessRunner
    {
        public int Starts { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(script[Math.Min(Starts++, script.Length - 1)]);
    }

    private static ProcessResult NeverStarted() => new(FailedToStart, [], "", TimedOut: false);

    private static ProcessResult Ran() => new(0, [Init, Allowed, Finished], "", TimedOut: false);

    private static MemoryGate Gate(params long[] available)
    {
        Queue<long> readings = new(available);
        return new MemoryGate(100, () => readings.Count > 1 ? readings.Dequeue() : readings.Peek(), TextWriter.Null, (_, _) => Task.CompletedTask);
    }

    private static AgentRun Run(params string[] cited) =>
        new(AgentOutcome.Answered, "270 days", cited,
            [new ToolTrace("memory_remember", new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("v") }, "held")],
            new TokenUsage(1, 2, 3, 4, 0.5), 3, null);

    private static RunScore Score() => new(1, false, true, true, 0.5, 2, 3, 1, 10, 20, 30);

    private static async Task<(AgentRun Run, int Starts)> RunAgent(MemoryGate? gate, params ProcessResult[] script)
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        ScriptedRunner runner = new(script);
        ClaudeCliAgent agent = new(bridge, "claude-sonnet-5-5", "medium", ".", runner) { Gate = gate };
        AgentRun run = await agent.RunAsync(new NoTools(), "sys", "q?", CancellationToken.None);
        return (run, runner.Starts);
    }

    [Fact]
    public async Task WaitForRoom_EnoughMemory_ReturnsWithoutAHold()
    {
        MemoryGate gate = Gate(100);

        await gate.WaitForRoomAsync(CancellationToken.None);

        gate.Holds.Should().Be(0);
    }

    [Fact]
    public async Task WaitForRoom_LowMemory_HoldsUntilTheFloorIsReached()
    {
        long[] readings = [50, 50, 99, 100];
        int read = 0;
        int waits = 0;
        MemoryGate gate = new(100, () => readings[Math.Min(read++, readings.Length - 1)], TextWriter.Null, (wait, _) =>
        {
            wait.Should().Be(MemoryGate.CheckEvery);
            waits++;
            return Task.CompletedTask;
        });

        await gate.WaitForRoomAsync(CancellationToken.None);

        waits.Should().Be(2);
        gate.Holds.Should().Be(1);
    }

    [Fact]
    public async Task WaitForRoom_Cancelled_StopsHolding()
    {
        using CancellationTokenSource cts = new();
        MemoryGate gate = new(100, () => 1, TextWriter.Null, (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await FluentActions.Awaiting(() => gate.WaitForRoomAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ForThisMachine_ReadsTheMachinesMemory()
    {
        // A floor of 0 GB always has room, so this proves the system read works without depending on what is free now.
        MemoryGate gate = MemoryGate.ForThisMachine(0, TextWriter.Null);

        await gate.WaitForRoomAsync(CancellationToken.None);

        gate.Holds.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ClaudeFailsToStart_StartsItAgainAndDoesNotScoreTheFailure()
    {
        MemoryGate gate = Gate(100);

        (AgentRun run, int starts) = await RunAgent(gate, NeverStarted(), NeverStarted(), Ran());

        starts.Should().Be(3);
        run.Outcome.Should().Be(AgentOutcome.NoAnswer);
        gate.FailedStarts.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_ClaudeNeverStarts_ScoresTheErrorAfterTheLastStart()
    {
        (AgentRun run, int starts) = await RunAgent(Gate(100), NeverStarted());

        starts.Should().Be(MemoryGate.MaxStarts);
        run.Outcome.Should().Be(AgentOutcome.Error);
    }

    [Fact]
    public async Task RunAsync_NoGate_ScoresAFailedStartStraightAway()
    {
        (AgentRun run, int starts) = await RunAgent(null, NeverStarted(), Ran());

        starts.Should().Be(1);
        run.Outcome.Should().Be(AgentOutcome.Error);
    }

    [Fact]
    public async Task RunAsync_TimedOut_IsNotStartedAgain()
    {
        (AgentRun run, int starts) = await RunAgent(Gate(100), new ProcessResult(-1, [], "", TimedOut: true), Ran());

        starts.Should().Be(1);
        run.Error.Should().Be("claude timed out");
    }

    [Fact]
    public async Task RunAsync_LowMemory_NoAgentStartsUntilThereIsRoom()
    {
        await using ToolBridge bridge = new();
        await bridge.StartAsync(CancellationToken.None);
        ScriptedRunner runner = new(Ran());
        int startsDuringHold = -1;
        long available = 0;
        MemoryGate gate = new(100, () => available, TextWriter.Null, (_, _) =>
        {
            startsDuringHold = runner.Starts;
            available = 200;
            return Task.CompletedTask;
        });
        ClaudeCliAgent agent = new(bridge, "claude-sonnet-5-5", "medium", ".", runner) { Gate = gate };

        await agent.RunAsync(new NoTools(), "sys", "q?", CancellationToken.None);

        startsDuringHold.Should().Be(0);
        runner.Starts.Should().Be(1);
    }

    [Fact]
    public void Checkpoint_ReadAfterARestart_GivesBackTheRunWithCitationsAsDatasetIds()
    {
        string path = Path.GetTempFileName();
        try
        {
            new RunCheckpoint(path).Save("on|t1|0", Run("11111111-1111-1111-1111-111111111111", "notes.txt"), Score(), Index);

            RunCheckpoint restarted = new(path);
            restarted.Restored.Should().Be(1);
            (AgentRun run, RunScore score) = restarted.Get("on|t1|0");
            run.Cited.Should().Equal("doc-1", "notes.txt");
            run.Calls.Should().ContainSingle().Which.Output.Should().Be("held");
            run.Calls[0].Input["value"].GetString().Should().Be("v");
            run.Usage.Should().Be(new TokenUsage(1, 2, 3, 4, 0.5));
            score.Should().Be(Score());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Checkpoint_LastLineCutShortByAKill_KeepsEveryWholeLineAndLaterSaves()
    {
        string path = Path.GetTempFileName();
        try
        {
            new RunCheckpoint(path).Save("a", Run(), Score(), Index);
            File.AppendAllText(path, """{"key":"b","run":{"Outc""");

            new RunCheckpoint(path).Save("c", Run(), Score(), Index);

            RunCheckpoint restarted = new(path);
            restarted.TryGet("a", out _).Should().BeTrue();
            restarted.TryGet("b", out _).Should().BeFalse();
            restarted.TryGet("c", out _).Should().BeTrue();
            restarted.Restored.Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Checkpoint_RunSavedTwice_CountsOnceAndKeepsTheLatest()
    {
        string path = Path.GetTempFileName();
        try
        {
            RunCheckpoint first = new(path);
            first.Save("a", Run("first"), Score(), Index);
            first.Save("a", Run("second"), Score(), Index);

            RunCheckpoint restarted = new(path);

            restarted.Restored.Should().Be(1);
            restarted.Get("a").Run.Cited.Should().Equal("second");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RequireSame_DifferentValueAfterARestart_StopsTheRun()
    {
        string path = Path.GetTempFileName();
        try
        {
            new RunCheckpoint(path).RequireSame("run", "split test");

            RunCheckpoint restarted = new(path);
            restarted.RequireSame("run", "split test");

            FluentActions.Invoking(() => restarted.RequireSame("run", "split dev")).Should().Throw<InvalidOperationException>().WithMessage("*mix*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mark_ReadAfterARestart_IsThere()
    {
        string path = Path.GetTempFileName();
        try
        {
            new RunCheckpoint(path).Mark("rep 1");

            new RunCheckpoint(path).HasMark("rep 1").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Checkpoint_NoPath_KeepsRunsInMemoryOnly()
    {
        RunCheckpoint checkpoint = new(RunCheckpoint.PathFor(null));

        checkpoint.Save("a", Run(), Score(), Index);

        checkpoint.TryGet("a", out _).Should().BeTrue();
        checkpoint.Restored.Should().Be(0);
    }

    [Fact]
    public void Report_CountsWhatTheGuardDid()
    {
        JsonObject report = Gate(100).Report(restoredRuns: 7);

        ((int)report["restoredRuns"]!).Should().Be(7);
        ((int)report["holds"]!).Should().Be(0);
    }
}
