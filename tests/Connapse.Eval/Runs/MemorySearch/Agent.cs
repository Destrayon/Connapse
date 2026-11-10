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
    string? Error,
    JsonElement? AnswerValue = null);

/// <summary>
/// A suite's rules: how many tool calls a run may make and the answer tool it ends with. A typed answer takes any JSON
/// value, recorded in <see cref="AgentRun.AnswerValue"/>; otherwise the answer is a string.
/// </summary>
public sealed record AgentRules(int MaxToolCalls, ToolSpec Answer, bool TypedAnswer)
{
    public static readonly AgentRules Default = new(AgentContract.MaxToolCalls, AgentContract.Answer, TypedAnswer: false);
}

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

    /// <summary>
    /// The answer tool for typed answers (CON-E6): a number, string, boolean or list, graded by the DataBench rules.
    /// Citations are optional, since a computed answer may rest on no single document.
    /// </summary>
    public static readonly ToolSpec TypedAnswer = new(
        AnswerTool,
        "Give your final answer. Call this once, when you are done; it ends the task.",
        new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement(new
            {
                type = new[] { "number", "string", "boolean", "array" },
                items = new { type = new[] { "number", "string" } },
                description = "The answer as a typed JSON value: a number (no units, no thousands separators), a string, "
                    + "true or false, or a list of numbers or strings",
            }),
            ["documentIds"] = JsonSerializer.SerializeToElement(new
            {
                type = "array",
                items = new { type = "string" },
                description = "The DocumentId (UUID) of every document your answer relies on",
            }),
        },
        ["answer"]);

    /// <summary>The answer value when it is present and not null, and the cited ids.</summary>
    public static (JsonElement? Answer, List<string> Cited) ReadTypedAnswer(IReadOnlyDictionary<string, JsonElement> input)
    {
        JsonElement? answer = input.TryGetValue("answer", out JsonElement a) && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? a.Clone()
            : null;
        return (answer, ReadCited(input));
    }

    /// <summary>The answer text and cited ids; a lone string where an array belongs counts as one id.</summary>
    public static (string? Answer, List<string> Cited) ReadAnswer(IReadOnlyDictionary<string, JsonElement> input)
    {
        string? answer = input.TryGetValue("answer", out JsonElement a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        return (answer, ReadCited(input));
    }

    /// <summary>The cited document ids, then the cited memory fact ids (the memory-agent suite's answer cites facts).</summary>
    private static List<string> ReadCited(IReadOnlyDictionary<string, JsonElement> input)
    {
        List<string> cited = [];
        foreach (string key in (string[])["documentIds", "factIds"])
        {
            if (!input.TryGetValue(key, out JsonElement ids))
                continue;
            if (ids.ValueKind == JsonValueKind.Array)
                cited.AddRange(ids.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            else if (ids.ValueKind == JsonValueKind.String)
                cited.Add(ids.GetString()!);
        }
        return cited;
    }
}
