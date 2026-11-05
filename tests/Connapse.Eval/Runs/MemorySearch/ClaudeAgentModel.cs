using System.Text.Json;
using Anthropic;
using Api = Anthropic.Models.Messages;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// The agent's model: one Messages API call per turn, with adaptive thinking (the model's default; it rejects a
/// temperature) at the given effort, and automatic prompt caching so each turn rereads the conversation cheaply.
/// Thinking blocks go back unchanged, signature included.
/// </summary>
public sealed class ClaudeAgentModel(AnthropicClient client, string model, string effort, int maxTokens = 8000) : IAgentModel
{
    public async Task<ModelReply> ReplyAsync(string system, IReadOnlyList<ToolSpec> tools, IReadOnlyList<AgentMessage> messages, CancellationToken ct)
    {
        Api.MessageCreateParams request = new()
        {
            Model = model,
            MaxTokens = maxTokens,
            System = new List<Api.TextBlockParam> { new() { Text = system } },
            Tools = [.. tools.Select(t => (Api.ToolUnion)new Api.Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new() { Properties = new Dictionary<string, JsonElement>(t.Properties), Required = [.. t.Required] },
            })],
            Messages = [.. messages.Select(ToParam)],
            OutputConfig = new Api.OutputConfig { Effort = ParseEffort(effort) },
            CacheControl = new Api.CacheControlEphemeral(),
        };

        Api.Message reply = await client.Messages.Create(request, ct);
        List<AgentBlock> blocks = [];
        foreach (Api.ContentBlock block in reply.Content)
        {
            if (block.TryPickText(out Api.TextBlock? text))
                blocks.Add(new AgentText(text.Text));
            else if (block.TryPickThinking(out Api.ThinkingBlock? thinking))
                blocks.Add(new AgentThinking(thinking.Thinking, thinking.Signature));
            else if (block.TryPickRedactedThinking(out Api.RedactedThinkingBlock? redacted))
                blocks.Add(new AgentRedactedThinking(redacted.Data));
            else if (block.TryPickToolUse(out Api.ToolUseBlock? call))
                blocks.Add(new AgentToolCall(call.ID, call.Name, call.Input));
        }

        TokenUsage usage = new(reply.Usage.InputTokens, reply.Usage.OutputTokens,
            reply.Usage.CacheReadInputTokens ?? 0, reply.Usage.CacheCreationInputTokens ?? 0);
        return new ModelReply(blocks, StopReason(reply), usage);
    }

    private static string StopReason(Api.Message reply) =>
        reply.StopReason == "refusal" ? "refusal"
        : reply.StopReason == "tool_use" ? "tool_use"
        : reply.StopReason == "max_tokens" ? "max_tokens"
        : "end_turn";

    private static Api.Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Api.Effort.Low,
        "medium" => Api.Effort.Medium,
        "high" => Api.Effort.High,
        "max" => Api.Effort.Max,
        _ => throw new ArgumentException($"Unknown effort '{effort}': low, medium, high or max."),
    };

    private static Api.MessageParam ToParam(AgentMessage message)
    {
        List<Api.ContentBlockParam> content = [.. message.Blocks.Select(ToParam)];
        return message.Role == AgentRole.User
            ? new Api.UserMessageParam { Content = content }
            : new Api.AssistantMessageParam { Content = content };
    }

    private static Api.ContentBlockParam ToParam(AgentBlock block) => block switch
    {
        AgentText t => new Api.TextBlockParam { Text = t.Text },
        AgentThinking t => new Api.ThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature },
        AgentRedactedThinking r => new Api.RedactedThinkingBlockParam { Data = r.Data },
        AgentToolCall c => new Api.ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = new Dictionary<string, JsonElement>(c.Input) },
        AgentToolResult r => new Api.ToolResultBlockParam { ToolUseID = r.CallId, Content = r.Content },
        _ => throw new ArgumentException($"Unknown block {block.GetType().Name}"),
    };
}
