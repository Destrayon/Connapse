using System.Text.Json;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>A piece of a model turn, independent of any model's SDK so the loop can be tested with a script.</summary>
public abstract record AgentBlock;

public sealed record AgentText(string Text) : AgentBlock;

public sealed record AgentToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input) : AgentBlock;

/// <summary>Thinking the model must get back unchanged on the next turn, signature included.</summary>
public sealed record AgentThinking(string Thinking, string Signature) : AgentBlock;

public sealed record AgentRedactedThinking(string Data) : AgentBlock;

public sealed record AgentToolResult(string CallId, string Content) : AgentBlock;

public enum AgentRole { User, Assistant }

public sealed record AgentMessage(AgentRole Role, IReadOnlyList<AgentBlock> Blocks);

public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    public TokenUsage Add(TokenUsage other) =>
        new(Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheWrite + other.CacheWrite);
}

public sealed record ModelReply(IReadOnlyList<AgentBlock> Blocks, string StopReason, TokenUsage Usage);

/// <summary>A tool as the model sees it: JSON Schema properties and the required ones.</summary>
public sealed record ToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties, IReadOnlyList<string> Required);

public interface IAgentModel
{
    Task<ModelReply> ReplyAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<AgentMessage> messages, CancellationToken ct);
}

public interface IToolbox
{
    IReadOnlyList<ToolSpec> Tools { get; }

    Task<string> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
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
/// The agent loop both arms run: the model calls tools until it calls <c>answer</c>. A run ends with no answer when
/// the model stops without calling a tool, out of calls when it asks for more than <see cref="MaxToolCalls"/> tool
/// calls, refused on a refusal, and in error when the model or a tool throws. Only an answer can score.
/// </summary>
public static class AgentLoop
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

    public static async Task<AgentRun> RunAsync(IAgentModel model, IToolbox toolbox, string system, string question, CancellationToken ct)
    {
        List<ToolSpec> tools = [.. toolbox.Tools, Answer];
        List<AgentMessage> messages = [new(AgentRole.User, [new AgentText(question)])];
        List<ToolTrace> calls = [];
        TokenUsage usage = TokenUsage.Zero;
        int turns = 0;

        AgentRun End(AgentOutcome outcome, string? error = null, string? answer = null, IReadOnlyList<string>? cited = null) =>
            new(outcome, answer, cited ?? [], calls, usage, turns, error);

        while (true)
        {
            ModelReply reply;
            try
            {
                reply = await model.ReplyAsync(system, tools, messages, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return End(AgentOutcome.Error, $"model: {ex.GetType().Name}: {ex.Message}");
            }
            turns++;
            usage = usage.Add(reply.Usage);
            if (reply.StopReason == "refusal")
                return End(AgentOutcome.Refused);

            List<AgentToolCall> requested = [.. reply.Blocks.OfType<AgentToolCall>()];
            if (requested.FirstOrDefault(c => c.Name == AnswerTool) is { } answer)
            {
                (string? text, List<string> cited) = ReadAnswer(answer.Input);
                return End(AgentOutcome.Answered, answer: text, cited: cited);
            }
            if (requested.Count == 0)
                return End(AgentOutcome.NoAnswer);

            messages.Add(new AgentMessage(AgentRole.Assistant, reply.Blocks));
            List<AgentBlock> results = [];
            foreach (AgentToolCall call in requested)
            {
                if (calls.Count >= MaxToolCalls)
                    return End(AgentOutcome.OutOfCalls);
                string output;
                try
                {
                    output = await toolbox.CallAsync(call.Name, call.Input, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    calls.Add(new ToolTrace(call.Name, call.Input, $"(threw {ex.GetType().Name})"));
                    return End(AgentOutcome.Error, $"{call.Name}: {ex.GetType().Name}: {ex.Message}");
                }
                calls.Add(new ToolTrace(call.Name, call.Input, output));
                results.Add(new AgentToolResult(call.Id, output));
            }
            messages.Add(new AgentMessage(AgentRole.User, results));
        }
    }

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
