using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Eval.Runs.MemorySearch;

/// <summary>
/// One agent run's view of the tools: its toolbox plus <c>answer</c>, the calls it made, and how it ended. Calls run
/// one at a time in arrival order. The run closes on its answer, on the call past <see cref="AgentContract.MaxToolCalls"/>,
/// or when a tool throws; every call after that is refused and recorded nowhere, so the outcome is the first one.
/// </summary>
public sealed class BridgeSession(IToolbox toolbox, AgentRules? rules = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ToolTrace> _calls = [];
    private readonly AgentRules _rules = rules ?? AgentRules.Default;

    public const string TaskOver = "The task is over. Do not call any more tools; stop now.";

    /// <summary>
    /// The toolbox's tools and the answer tool. The answer tool is marked to load up front, so a client that defers
    /// tools behind its own tool search never has to search for it.
    /// </summary>
    public IReadOnlyList<ToolSpec> Tools { get; } = [.. toolbox.Tools, AlwaysLoaded((rules ?? AgentRules.Default).Answer)];

    public string? Instructions => toolbox.Instructions;

    private static ToolSpec AlwaysLoaded(ToolSpec answer)
    {
        JsonObject wire = ToolBridge.ToolJson(answer).AsObject();
        wire["_meta"] = new JsonObject { ["anthropic/alwaysLoad"] = true };
        return answer with { Wire = wire };
    }

    public IReadOnlyList<ToolTrace> Calls => _calls;

    /// <summary>Answered, OutOfCalls or Error once the run closed; null while it is open.</summary>
    public AgentOutcome? Outcome { get; private set; }

    public string? Answer { get; private set; }

    /// <summary>The answer as the agent gave it, under typed rules; <see cref="Answer"/> then holds its raw JSON.</summary>
    public JsonElement? AnswerValue { get; private set; }

    public IReadOnlyList<string> Cited { get; private set; } = [];

    public string? Error { get; private set; }

    public async Task<(string Text, bool IsError)> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (Outcome is not null)
                return (TaskOver, true);
            if (name == AgentContract.AnswerTool && _rules.TypedAnswer)
            {
                (JsonElement? value, List<string> typedCited) = AgentContract.ReadTypedAnswer(input);
                if (value is not { } v)
                    return ("Error: Missing required argument 'answer': give the answer as a JSON value.", true);
                (Outcome, Answer, AnswerValue, Cited) = (AgentOutcome.Answered, v.GetRawText(), v, typedCited);
                return ("Answer recorded. " + TaskOver, false);
            }
            if (name == AgentContract.AnswerTool)
            {
                (string? text, List<string> cited) = AgentContract.ReadAnswer(input);
                if (text is null)
                    return ("Error: Missing required argument 'answer': give the answer as a string.", true);
                (Outcome, Answer, Cited) = (AgentOutcome.Answered, text, cited);
                return ("Answer recorded. " + TaskOver, false);
            }
            if (_calls.Count >= _rules.MaxToolCalls)
            {
                Outcome = AgentOutcome.OutOfCalls;
                return ($"You have used all {_rules.MaxToolCalls} tool calls. " + TaskOver, true);
            }
            ToolOutput output;
            try
            {
                output = await toolbox.InvokeAsync(name, input, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _calls.Add(new ToolTrace(name, input, $"(threw {ex.GetType().Name})"));
                (Outcome, Error) = (AgentOutcome.Error, $"{name}: {ex.GetType().Name}: {ex.Message}");
                return ("The tool failed. " + TaskOver, true);
            }
            _calls.Add(new ToolTrace(name, input, output.Text, output.IsError));
            return (output.Text, output.IsError);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A minimal MCP server (Streamable HTTP, JSON responses, no SSE) on loopback, so a Claude Code agent reaches the
/// eval's in-process tools as an ordinary MCP server. Each run gets its own path, <c>/mcp/{session}</c>.
/// </summary>
public sealed class ToolBridge : IAsyncDisposable
{
    public const string ServerName = "connapse";

    private readonly ConcurrentDictionary<string, BridgeSession> _sessions = new(StringComparer.Ordinal);
    private WebApplication? _app;

    /// <summary>The base address, e.g. <c>http://127.0.0.1:51234</c>, once started.</summary>
    public string? BaseUrl { get; private set; }

    public (string Id, BridgeSession Session) Open(IToolbox toolbox, AgentRules? rules = null)
    {
        string id = Guid.NewGuid().ToString("N");
        BridgeSession session = new(toolbox, rules);
        _sessions[id] = session;
        return (id, session);
    }

    public void Close(string id) => _sessions.TryRemove(id, out _);

    public string UrlFor(string id) => $"{BaseUrl ?? throw new InvalidOperationException("The bridge is not started.")}/mcp/{id}";

    /// <summary>The JSON-RPC response to one message, or null for a notification. Throws when the session is unknown.</summary>
    public async Task<JsonObject?> HandleAsync(string sessionId, JsonObject message, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(sessionId, out BridgeSession? session))
            throw new KeyNotFoundException($"No session {sessionId}.");
        JsonNode? id = message["id"]?.DeepClone();
        string? method = message["method"]?.GetValue<string>();
        if (id is null)
            return null;

        JsonObject Result(JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        JsonObject Fail(int code, string text) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };

        switch (method)
        {
            case "initialize":
                JsonObject initialized = new()
                {
                    ["protocolVersion"] = message["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "eval" },
                };
                if (session.Instructions is { } instructions)
                    initialized["instructions"] = instructions;
                return Result(initialized);
            case "ping":
                return Result(new JsonObject());
            case "tools/list":
                return Result(new JsonObject { ["tools"] = new JsonArray([.. session.Tools.Select(ToolJson)]) });
            case "tools/call":
                string name = message["params"]?["name"]?.GetValue<string>() ?? "";
                Dictionary<string, JsonElement> input = message["params"]?["arguments"] is JsonObject args
                    ? args.Deserialize<Dictionary<string, JsonElement>>()!
                    : [];
                (string text, bool isError) = await session.CallAsync(name, input, ct);
                return Result(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
            default:
                return Fail(-32601, $"Method not found: {method}");
        }
    }

    public static JsonNode ToolJson(ToolSpec tool) => tool.Wire?.DeepClone() ?? new JsonObject
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(tool.Properties.Select(p => KeyValuePair.Create(p.Key, JsonNode.Parse(p.Value.GetRawText())))),
            ["required"] = new JsonArray([.. tool.Required.Select(r => (JsonNode)r)]),
        },
    };

    public async Task StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        app.MapPost("/mcp/{session}", async (string session, HttpContext http) =>
        {
            JsonObject? message;
            try
            {
                message = await JsonSerializer.DeserializeAsync<JsonObject>(http.Request.Body, cancellationToken: http.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (message is null)
                return Results.BadRequest();
            try
            {
                JsonObject? reply = await HandleAsync(session, message, http.RequestAborted);
                return reply is null ? Results.Accepted() : Results.Text(reply.ToJsonString(), "application/json");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });
        app.MapMethods("/mcp/{session}", ["GET", "DELETE"], () => Results.StatusCode(405));
        await app.StartAsync(ct);
        _app = app;
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = address.Replace("[::1]", "127.0.0.1").TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
