using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Connapse.Identity.Authentication;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// Proof tests for #793 (P6): what each MCP tool demands, and what a scoped agent key can see and
/// run. The tools section of authorization-expectations.json says which scope each tool needs and
/// its risk; today no tool carries a scope, so every gap is listed in known-authz-violations.json.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpAuthorizationTests(SharedWebAppFixture fixture)
{
    private static IReadOnlyDictionary<string, AuthzExpectation> Tools => AuthzExpectations.Table.Value.Tools;

    [Fact]
    public async Task EveryMcpTool_HasScopeAndRiskMetadata()
    {
        await using var admin = await ConnectAsync(fixture.AdminClient.DefaultRequestHeaders.Authorization!);
        var listed = (await admin.ListToolsAsync()).ToDictionary(t => t.Name);

        listed.Keys.Should().BeEquivalentTo(Tools.Keys, "every MCP tool needs an entry in the tools section of authorization-expectations.json");

        var violations = new List<string>();
        foreach (var (name, expected) in Tools)
        {
            string declaredScope = DeclaredScope(name) ?? "none";
            if (declaredScope != expected.Scope)
                violations.Add($"mcp-tool {name}: expects scope {expected.Scope}, declares {declaredScope}");

            // The risk a client sees: MCP treats a tool with no destructive hint as destructive.
            var hints = listed[name].ProtocolTool.Annotations;
            string declaredRisk = hints?.ReadOnlyHint == true
                ? (hints.DestructiveHint == true ? "read+destructive" : "read")
                : hints?.DestructiveHint == false ? "write" : "destructive";
            if (declaredRisk != expected.Risk)
                violations.Add($"mcp-tool {name}: expects risk {expected.Risk}, declares {declaredRisk}");
        }

        AuthzExpectations.ShouldMatchKnown(violations, "mcp-tool ");
    }

    [Fact]
    public async Task ToolsList_ReadOnlyScopeToken_ExcludesWriteAndDestructiveTools()
    {
        await using var agent = await ConnectAsync(AuthzPrincipal.AgentRead);

        var violations = (await agent.ListToolsAsync())
            .Where(t => !Visible(AuthzPrincipal.AgentRead, t.Name))
            .Select(t => $"tools/list as agent-read sees {t.Name}");

        AuthzExpectations.ShouldMatchKnown(violations, "tools/list ");
    }

    /// <summary>
    /// Calls every tool a knowledge:read key shouldn't see, by name, with arguments that name
    /// nothing real, so a tool that runs anyway only reports that its target is missing.
    /// </summary>
    [Fact]
    public async Task ToolsCall_HiddenToolByName_ReturnsForbidden()
    {
        await using var agent = await ConnectAsync(AuthzPrincipal.AgentRead);
        string missing = $"authz-missing-{Guid.NewGuid():N}";
        Dictionary<string, Dictionary<string, object?>> harmless = new()
        {
            ["container_create"] = new() { ["name"] = "NOT A VALID NAME" },
            ["container_delete"] = new() { ["containerId"] = missing },
            ["upload_file"] = new() { ["containerId"] = missing, ["textContent"] = "probe", ["fileName"] = "probe.txt" },
            ["delete_file"] = new() { ["containerId"] = missing, ["fileId"] = Guid.NewGuid().ToString() },
            ["bulk_delete"] = new() { ["containerId"] = missing, ["fileIds"] = $"[\"{Guid.NewGuid()}\"]" },
            ["bulk_upload"] = new() { ["containerId"] = missing, ["files"] = "[{\"filename\":\"probe.txt\",\"content\":\"probe\"}]" },
        };

        var hidden = Tools.Keys.Where(t => !Visible(AuthzPrincipal.AgentRead, t)).Order().ToList();
        hidden.Should().BeEquivalentTo(harmless.Keys, "each hidden tool needs harmless arguments here");

        var violations = new List<string>();
        foreach (string tool in hidden)
        {
            if (!await RefusedAsync(async () => await agent.CallToolAsync(tool, harmless[tool])))
                violations.Add($"tools/call as agent-read runs {tool}");
        }

        AuthzExpectations.ShouldMatchKnown(violations, "tools/call ");
    }

    /// <summary>
    /// A destructive tool called by a key that holds its scope must come back pending approval,
    /// with its target untouched, until a person approves it (#782).
    /// </summary>
    [Fact]
    public async Task ToolsCall_DestructiveTool_ReturnsPendingApproval_AndNothingChanged()
    {
        await using var agent = await ConnectAsync(AuthzPrincipal.AgentWrite);
        var http = fixture.AdminClient;
        string empty = await CreateContainerAsync(http, "authz-approve-empty");
        string full = await CreateContainerAsync(http, "authz-approve-full");
        try
        {
            string first = await UploadAsync(http, full, "first.txt");
            string second = await UploadAsync(http, full, "second.txt");

            (string Tool, Dictionary<string, object?> Arguments, Func<Task<bool>> Unchanged)[] cases =
            [
                ("container_delete", new() { ["containerId"] = empty },
                    async () => (await http.GetAsync($"/api/containers/{empty}")).IsSuccessStatusCode),
                ("delete_file", new() { ["containerId"] = full, ["fileId"] = first },
                    async () => (await http.GetAsync($"/api/containers/{full}/files/{first}")).IsSuccessStatusCode),
                ("bulk_delete", new() { ["containerId"] = full, ["fileIds"] = $"[\"{second}\"]" },
                    async () => (await http.GetAsync($"/api/containers/{full}/files/{second}")).IsSuccessStatusCode),
            ];
            Tools.Where(t => t.Value.Risk == "destructive").Select(t => t.Key)
                .Should().BeEquivalentTo(cases.Select(c => c.Tool), "each destructive tool needs a case here");

            var violations = new List<string>();
            foreach (var (tool, arguments, unchanged) in cases)
            {
                var result = await agent.CallToolAsync(tool, arguments);
                string text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));
                // Until #782 defines the approval result, a successful result saying the call is
                // pending approval is the floor: an error that merely mentions approval isn't one.
                if (result.IsError == true || !text.Contains("pending approval", StringComparison.OrdinalIgnoreCase))
                    violations.Add($"approval {tool}: expects pending approval, ran without it");
                if (!await unchanged())
                    violations.Add($"approval {tool}: expects its target unchanged, it was deleted");
            }

            AuthzExpectations.ShouldMatchKnown(violations, "approval ");
        }
        finally
        {
            foreach (string container in new[] { empty, full })
            {
                using var listed = await http.GetAsync($"/api/containers/{container}/files");
                if (!listed.IsSuccessStatusCode)
                    continue; // the tool under test already deleted it
                var files = await listed.Content.ReadFromJsonAsync<JsonElement>();
                if (files.ValueKind == JsonValueKind.Array)
                    foreach (var file in files.EnumerateArray())
                        await http.DeleteAsync($"/api/containers/{container}/files/{file.GetProperty("id").GetString()}");
                await http.DeleteAsync($"/api/containers/{container}");
            }
        }
    }

    /// <summary>Whether a tool should be listed for a scoped key: it holds the tool's scope.</summary>
    private static bool Visible(AuthzPrincipal principal, string tool) =>
        Tools[tool].Scope is { } scope && principal.Scopes!.Contains(scope);

    /// <summary>The scope a tool's [Authorize] policy demands, in the table's words; null when none.</summary>
    private static string? DeclaredScope(string tool)
    {
        var method = typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        var policies = method.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(typeof(McpTools).GetCustomAttributes<AuthorizeAttribute>())
            .Select(a => a.Policy).OfType<string>().ToList();
        return policies.Count == 0 ? null : string.Join("+", policies.Select(p => p switch
        {
            "Scope:KnowledgeRead" => "knowledge:read",
            "Scope:KnowledgeWrite" => "knowledge:write",
            "Scope:AgentIngest" => "agent:ingest",
            _ => p,
        }));
    }

    /// <summary>
    /// Refused means the protocol error the SDK's authorization filter raises for a tool the caller
    /// may not use ("Access forbidden: This tool requires authorization."), or the unknown-tool error
    /// for one hidden from it. Any result, an error result included, means the tool ran.
    /// </summary>
    private static async Task<bool> RefusedAsync(Func<Task> call)
    {
        try
        {
            await call();
            return false;
        }
        catch (ModelContextProtocol.McpProtocolException ex) when (ex.Message.Contains("Access forbidden", StringComparison.Ordinal)
            || ex.Message.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    private async Task<McpClient> ConnectAsync(AuthzPrincipal principal)
    {
        var http = fixture.Factory.CreateClient();
        (await AuthzPrincipals.CredentialAsync(fixture, principal))(http.DefaultRequestHeaders);
        return await ConnectAsync(http);
    }

    private Task<McpClient> ConnectAsync(AuthenticationHeaderValue bearer)
    {
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = bearer;
        return ConnectAsync(http);
    }

    private static Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));

    private static async Task<string> CreateContainerAsync(HttpClient http, string prefix)
    {
        var response = await http.PostAsJsonAsync("/api/containers", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> UploadAsync(HttpClient http, string containerId, string fileName)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent("approval probe"u8.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", fileName);
        var response = await http.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("documents")[0].GetProperty("documentId").GetString()!;
    }
}
