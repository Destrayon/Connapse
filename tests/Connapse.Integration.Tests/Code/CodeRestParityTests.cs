using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.CrossSurface;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;

namespace Connapse.Integration.Tests.Code;

/// <summary>
/// CON-284 through the real host: a script asks the code routes what an agent asks the code tools, acting as the same
/// user, and gets the same answer; what the tools refuse, the routes refuse too.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CodeRestParityTests(SharedWebAppFixture fixture)
{
    private const string AuditLog = """
        namespace Acme.Audit;

        public static class AuditLog
        {
            public static void Write(object entry) { }
        }
        """;

    private const string Ledger = """
        using Acme.Audit;

        namespace Acme.Billing;

        public class Ledger
        {
            public void Post(object entry)
            {
                AuditLog.Write(entry);
            }
        }
        """;

    private const string Invoices = """
        namespace Acme.Billing;

        public class Invoices
        {
            private readonly Ledger _ledger = new();

            public void Close(object invoice)
            {
                _ledger.Post(invoice);
            }
        }
        """;

    private const string LedgerTests = """
        using Acme.Billing;

        namespace Acme.Tests;

        public class LedgerTests
        {
            public void Post_Writes()
            {
                Ledger ledger = new Ledger();
                ledger.Post(1);
            }
        }
        """;

    [Fact]
    public async Task EveryCodeQuestion_OverRest_MatchesTheCodeTool()
    {
        HttpClient rest = fixture.AdminClient;
        string name = $"code-parity-{Guid.NewGuid():N}"[..40];
        string container = await CreateContainerAsync(rest, name, kind: null);
        await IngestAsync(container, "/acme/AuditLog.cs", AuditLog);
        await IngestAsync(container, "/acme/Ledger.cs", Ledger);
        await IngestAsync(container, "/acme/Invoices.cs", Invoices);
        await IngestAsync(container, "/tests/LedgerTests.cs", LedgerTests);
        await using McpClient mcp = await ConnectAsync(
            await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read"));

        JsonNode symbol = await SameAsync(rest, mcp,
            $"symbols?containerId={container}&query=Ledger.Post&follow=true",
            "code_find_symbol", new() { ["containerId"] = container, ["query"] = "Ledger.Post", ["follow"] = true });
        symbol["definitions"]![0]!["symbol"]!.GetValue<string>().Should().Be("Acme.Billing.Ledger.Post");
        symbol["definitions"]![0]!["path"]!.GetValue<string>().Should().Be("acme/Ledger.cs");
        symbol["uses"]!.AsArray().Select(u => u!["symbol"]!.GetValue<string>()).Should().Contain("Acme.Audit.AuditLog.Write");

        // A container named rather than numbered, and words rather than a name, read the same on both.
        await SameAsync(rest, mcp, $"symbols?containerId={name.ToUpperInvariant()}&query=audit%20write&match=Words",
            "code_find_symbol", new() { ["containerId"] = name.ToUpperInvariant(), ["query"] = "audit write", ["match"] = "Words" });

        JsonNode references = await SameAsync(rest, mcp, $"references?containerId={container}&symbol=Ledger.Post&kind=callers",
            "code_references", new() { ["containerId"] = container, ["symbol"] = "Ledger.Post", ["kind"] = "callers" });
        references["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Should().Contain("acme/Invoices.cs");

        JsonNode impact = await SameAsync(rest, mcp, $"impact?containerId={container}&symbol=Ledger.Post&depth=2",
            "code_impact", new() { ["containerId"] = container, ["symbol"] = "Ledger.Post", ["depth"] = 2 });
        impact["affected"]!.AsArray().Select(a => a!["symbol"]!.GetValue<string>()).Should().Contain("Acme.Billing.Invoices.Close");

        JsonNode map = await SameAsync(rest, mcp, $"map?containerId={container}&maxFiles=10",
            "code_repo_map", new() { ["containerId"] = container, ["maxFiles"] = 10 });
        map["totalFiles"]!.GetValue<int>().Should().BeGreaterThan(0);
        await SameAsync(rest, mcp, $"map?containerId={container}&path=/tests",
            "code_repo_map", new() { ["containerId"] = container, ["path"] = "/tests" });
    }

    [Fact]
    public async Task WhatTheToolsRefuse_TheRoutesRefuse()
    {
        HttpClient rest = fixture.AdminClient;
        string space = await CreateContainerAsync(rest, $"code-memory-{Guid.NewGuid():N}"[..40], kind: "memory");
        string missing = Guid.NewGuid().ToString();
        try
        {
            await using McpClient mcp = await ConnectAsync(
                await AgentKeyAsync(await Principals.UserIdAsync(fixture, Principal.Admin), "knowledge:read"));

            // Values the tools can't read are a 400 naming the field.
            await RefusedAsync(rest, $"symbols?containerId={space}&query=Post&match=fuzzy", HttpStatusCode.BadRequest, "match");
            await RefusedAsync(rest, $"references?containerId={space}&symbol=Post&kind=subtypes", HttpStatusCode.BadRequest, "kind");
            await RefusedAsync(rest, $"symbols?containerId={space}&query=%20", HttpStatusCode.BadRequest, null);
            (await CallAsync(mcp, "code_find_symbol", new() { ["containerId"] = space, ["query"] = "Post", ["match"] = "fuzzy" }))
                .Should().StartWith("Error: match must be");

            // An unknown container is not found, and a memory space has no code, on both.
            foreach (string route in new[] { "symbols?query=Post", "references?symbol=Post", "impact?symbol=Post", "map?x=1" })
            {
                await RefusedAsync(rest, $"{route}&containerId={missing}", HttpStatusCode.NotFound, null);
                await RefusedAsync(rest, $"{route}&containerId={space}", HttpStatusCode.Conflict, null);
            }
            await RefusedAsync(rest, "map", HttpStatusCode.NotFound, null);
            (await CallAsync(mcp, "code_repo_map", new() { ["containerId"] = missing })).Should().StartWith("Error").And.Contain("not found");
            (await CallAsync(mcp, "code_repo_map", new() { ["containerId"] = space })).Should().StartWith("Error");
        }
        finally
        {
            await DeleteContainersAsync(space);
        }
    }

    /// <summary>Asks one question over REST and over MCP, and returns the answer after checking the two are the same.</summary>
    private static async Task<JsonNode> SameAsync(HttpClient rest, McpClient mcp, string route, string tool, Dictionary<string, object?> arguments)
    {
        HttpResponseMessage response = await rest.GetAsync($"/api/code/{route}");
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        string answer = await CallAsync(mcp, tool, arguments);
        answer.Should().StartWith("{", answer);

        JsonNode overRest = WithoutNulls(JsonNode.Parse(body))!;
        JsonNode overMcp = WithoutNulls(JsonNode.Parse(answer))!;
        JsonNode.DeepEquals(overRest, overMcp).Should().BeTrue($"REST answered {body}\nand {tool} answered {answer}");
        return overRest;
    }

    /// <summary>The tools leave null properties out and the routes write them as null; both mean the same.</summary>
    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList())
                    obj.Remove(key);
                foreach (var property in obj)
                    WithoutNulls(property.Value);
                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                    WithoutNulls(item);
                break;
        }
        return node;
    }

    private static async Task RefusedAsync(HttpClient http, string route, HttpStatusCode status, string? field)
    {
        HttpResponseMessage response = await http.GetAsync($"/api/code/{route}");
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, $"{route} answered {body}");
        if (field is not null)
            JsonDocument.Parse(body).RootElement.GetProperty("field").GetString().Should().Be(field);
    }

    private async Task IngestAsync(string container, string path, string text)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            new IngestionOptions(FileName: Path.GetFileName(path), ContentType: "text/plain", Path: path)
            {
                Owner = OwnerRef.ForContainer(Guid.Parse(container)),
            });
    }

    private static async Task<string> CreateContainerAsync(HttpClient http, string name, string? kind)
    {
        HttpResponseMessage create = await http.PostAsJsonAsync("/api/containers", kind is null ? new { name } : (object)new { name, kind });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
    }

    private async Task DeleteContainersAsync(params string[] containers)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        foreach (string container in containers)
            await store.DeleteAsync(Guid.Parse(container));
    }

    /// <summary>A key for a new agent <paramref name="user"/> created, so it acts as that user.</summary>
    private async Task<Action<HttpRequestHeaders>> AgentKeyAsync(Guid user, params string[] scopes)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var agent = await agents.CreateAsync(new CreateAgentRequest($"code-{Guid.NewGuid():N}"), user);
        var key = await agents.CreateKeyAsync(agent!.Id, new CreateAgentKeyRequest("code", scopes, RequiresApproval: false));
        return headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, key!.Token);
    }

    private async Task<McpClient> ConnectAsync(Action<HttpRequestHeaders> credential)
    {
        var http = fixture.Factory.CreateClient();
        credential(http.DefaultRequestHeaders);
        return await McpTestClient.ConnectAsync(http);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}
