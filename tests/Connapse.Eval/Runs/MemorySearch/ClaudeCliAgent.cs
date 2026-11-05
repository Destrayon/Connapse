using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>What a finished process printed.</summary>
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Stdout, string Stderr, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs a process at below-normal priority, reading its output line by line; kills it on timeout.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // It already exited.
        }
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        List<string> lines = [];
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(limit.Token) is { } line)
                lines.Add(line);
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
                throw;
            return new ProcessResult(-1, lines, "", TimedOut: true);
        }
        return new ProcessResult(process.ExitCode, lines, await stderr, TimedOut: false);
    }
}

/// <summary>The subscription's rate-limit state Claude Code reported: its status and each window's utilization (0–1).</summary>
public sealed record SubscriptionLimit(string Status, IReadOnlyDictionary<string, double> Utilization)
{
    public bool Allowed => Status.StartsWith("allowed", StringComparison.Ordinal);

    public override string ToString() =>
        $"{Status} ({string.Join(", ", Utilization.OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => $"{u.Key} {u.Value:P0}"))})";
}

/// <summary>What one <c>claude -p</c> stream-json run reported.</summary>
public sealed record CliResult(
    bool Finished, bool IsError, string? Subtype, string? StopReason, int Turns, TokenUsage Usage, string? Text,
    IReadOnlyList<string> Tools, SubscriptionLimit? Limit)
{
    public static readonly CliResult None = new(false, false, null, null, 0, TokenUsage.Zero, null, [], null);

    /// <summary>Reads the init event (tools), the last rate-limit event and the result event; other lines are ignored.</summary>
    public static CliResult Parse(IEnumerable<string> lines)
    {
        CliResult parsed = None;
        foreach (string line in lines)
        {
            JsonObject? e;
            try
            {
                e = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            switch (e?["type"]?.GetValue<string>())
            {
                case "system" when e["subtype"]?.GetValue<string>() == "init":
                    parsed = parsed with { Tools = [.. (e["tools"] as JsonArray ?? []).Select(t => t?.GetValue<string>()).OfType<string>()] };
                    break;
                case "rate_limit_event" when e["rate_limit_info"] is JsonObject info:
                    Dictionary<string, double> windows = new(StringComparer.Ordinal);
                    if (info["unifiedWindows"] is JsonObject unified)
                        foreach ((string window, JsonNode? w) in unified)
                            if (w?["utilization"] is JsonValue u && u.TryGetValue(out double value))
                                windows[window] = value;
                    parsed = parsed with { Limit = new SubscriptionLimit(info["status"]?.GetValue<string>() ?? "unknown", windows) };
                    break;
                case "result":
                    parsed = parsed with
                    {
                        Finished = true,
                        IsError = e["is_error"]?.GetValue<bool>() ?? false,
                        Subtype = e["subtype"]?.GetValue<string>(),
                        StopReason = e["stop_reason"]?.GetValue<string>(),
                        Turns = e["num_turns"]?.GetValue<int>() ?? 0,
                        Usage = SumUsage(e["modelUsage"] as JsonObject),
                        Text = e["result"]?.GetValue<string>(),
                    };
                    break;
            }
        }
        return parsed;
    }

    private static TokenUsage SumUsage(JsonObject? models)
    {
        TokenUsage total = TokenUsage.Zero;
        foreach ((string _, JsonNode? m) in models ?? [])
            if (m is JsonObject u)
                total = total.Add(new TokenUsage(Long(u, "inputTokens"), Long(u, "outputTokens"), Long(u, "cacheReadInputTokens"),
                    Long(u, "cacheCreationInputTokens"), u["costUSD"]?.GetValue<double>() ?? 0));
        return total;
    }

    private static long Long(JsonObject o, string key) => o[key]?.GetValue<long>() ?? 0;
}

/// <summary>Thrown when the subscription's limit is reached, so the run stops instead of scoring failed agents.</summary>
public sealed class SubscriptionLimitException(SubscriptionLimit limit)
    : Exception($"The Claude subscription limit was reached: {limit}. Nothing past this point was scored.")
{
    public SubscriptionLimit Limit { get; } = limit;
}

/// <summary>
/// Runs each agent as a headless Claude Code session (<c>claude -p</c>) signed in with the user's subscription. Its
/// only tools are the run's <see cref="ToolBridge"/> session: no built-in tools, no settings files, CLAUDE.md, skills,
/// plugins or other MCP servers, and no API key, so the usage comes out of the subscription.
/// </summary>
public sealed class ClaudeCliAgent(
    ToolBridge bridge, string model, string effort, string workDir, IProcessRunner? runner = null,
    string executable = "claude", TimeSpan? timeout = null) : IAgentRunner
{
    /// <summary>Variables that would make Claude Code bill an API account instead of the subscription.</summary>
    public static readonly string[] RemovedVariables = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(10);
    private readonly Lock _lock = new();

    /// <summary>The subscription state the first and the latest run reported.</summary>
    public SubscriptionLimit? FirstLimit { get; private set; }

    public SubscriptionLimit? LastLimit { get; private set; }

    public async Task<AgentRun> RunAsync(IToolbox toolbox, string system, string question, CancellationToken ct)
    {
        (string id, BridgeSession session) = bridge.Open(toolbox);
        try
        {
            ProcessResult process = await _runner.RunAsync(StartInfo(bridge.UrlFor(id), system, question), _timeout, ct);
            CliResult result = CliResult.Parse(process.Stdout);
            if (result.Limit is { } limit)
                lock (_lock)
                    (FirstLimit, LastLimit) = (FirstLimit ?? limit, limit);
            if (result.Limit is { Allowed: false } blocked && session.Outcome is null)
                throw new SubscriptionLimitException(blocked);
            return Compose(session, result, process);
        }
        finally
        {
            bridge.Close(id);
        }
    }

    public ProcessStartInfo StartInfo(string mcpUrl, string system, string question)
    {
        ProcessStartInfo start = new(executable) { WorkingDirectory = workDir };
        foreach (string a in Arguments(mcpUrl, system, question, model, effort))
            start.ArgumentList.Add(a);
        foreach (string v in RemovedVariables)
            start.Environment.Remove(v);
        return start;
    }

    public static List<string> Arguments(string mcpUrl, string system, string question, string model, string effort)
    {
        string config = new JsonObject
        {
            ["mcpServers"] = new JsonObject { [ToolBridge.ServerName] = new JsonObject { ["type"] = "http", ["url"] = mcpUrl } },
        }.ToJsonString();
        return
        [
            "-p", question,
            "--model", model,
            "--effort", effort,
            "--system-prompt", system,
            "--mcp-config", config,
            "--strict-mcp-config",
            "--tools", "",
            "--allowedTools", $"mcp__{ToolBridge.ServerName}",
            "--permission-mode", "dontAsk",
            "--restricted",
            "--setting-sources", "",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--output-format", "stream-json",
            "--verbose",
        ];
    }

    /// <summary>
    /// The run as the scorer sees it. What the tools recorded comes first (an answer, running out of calls, a tool
    /// error); otherwise a refusal, a failed session, or a session that ended without an answer.
    /// </summary>
    public static AgentRun Compose(BridgeSession session, CliResult result, ProcessResult process)
    {
        AgentRun End(AgentOutcome outcome, string? error = null) =>
            new(outcome, session.Answer, session.Cited, session.Calls, result.Usage, result.Turns, error);

        if (session.Outcome is { } outcome)
            return End(outcome, session.Error);
        if (process.TimedOut)
            return End(AgentOutcome.Error, "claude timed out");
        if (!result.Finished)
            return End(AgentOutcome.Error, $"claude exited {process.ExitCode} without a result: {Tail(process.Stderr)}");
        if (!result.Tools.Contains($"mcp__{ToolBridge.ServerName}__{AgentContract.AnswerTool}"))
            return End(AgentOutcome.Error, $"the agent did not get the eval's tools ({string.Join(", ", result.Tools)})");
        if (result.StopReason == "refusal")
            return End(AgentOutcome.Refused);
        if (result.IsError)
            return End(AgentOutcome.Error, $"claude {result.Subtype}: {Tail(result.Text ?? "")}");
        return End(AgentOutcome.NoAnswer);
    }

    private static string Tail(string text) => text.Length <= 300 ? text.Trim() : text[^300..].Trim();
}
