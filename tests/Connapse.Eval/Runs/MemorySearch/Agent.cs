using System.Text.Json;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>Tokens one run used, and their list-price cost as Claude Code reports it (billed to the subscription).</summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite, double CostUsd = 0)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    public TokenUsage Add(TokenUsage other) =>
        new(Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheWrite + other.CacheWrite, CostUsd + other.CostUsd);
}

/// <summary>A tool as the model sees it: JSON Schema properties and the required ones.</summary>
public sealed record ToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required);

public interface IToolbox
{
    IReadOnlyList<ToolSpec> Tools { get; }

    Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
}

/// <summary>Runs one agent to the end of one question with the given tools.</summary>
public interface IAgentRunner
{
    Task<AgentRun> RunAsync(IToolbox toolbox, string system, string question, CancellationToken ct);
}

public enum AgentOutcome { Answered, NoAnswer, OutOfCalls, Refused, Error }

/// <summary>One tool call the agent made and what the tool returned.</summary>
public sealed record ToolTrace(string Name, IReadOnlyDictionary<string, JsonElement> Input, string Output);

/// <summary>How one run ended, what it cited, and what it cost.</summary>
public sealed record AgentRun(
    AgentOutcome Outcome,
    string? Answer,
    IReadOnlyList<string> Cited,
    IReadOnlyList<ToolTrace> Calls,
    TokenUsage Usage,
    int Turns,
    string? Error);

/// <summary>
/// The rules both arms run under: the agent calls tools until it calls <c>answer</c>. A run ends out of calls when
/// it asks for more than <see cref="MaxToolCalls"/> tool calls, and in error when a tool throws. Only an answer can
/// score.
/// </summary>
public static class AgentContract
{
    public const int MaxToolCalls = 12;
    public const string AnswerTool = "answer";

    public static readonly ToolSpec Answer = new(
        AnswerTool,
        "Give your final answer and the documents it comes from. Call this once, when you are done; it ends the task.",
        new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The short answer to the question" }),
            ["documentIds"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                description = "The DocumentId (UUID) of every document your answer relies on",
            }),
        },
        ["answer", "documentIds"]);

    /// <summary>The answer text and cited ids; a lone string where an array belongs counts as one id.</summary>
    public static (string? Answer, List<string> Cited) ReadAnswer(IReadOnlyDictionary<string, JsonElement> input)
    {
        string? answer = input.TryGetValue("answer", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        List<string> cited = [];
        if (input.TryGetValue("documentIds", out JsonElement ids))
        {
            if (ids.ValueKind == JsonValueKind.Array)
                cited.AddRange(ids.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            else if (ids.ValueKind == JsonValueKind.String)
                cited.Add(ids.GetString()!);
        }
        return (answer, cited);
    }
}
