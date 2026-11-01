using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// Proof test for #793 (P9, OWASP API1 object-level authorization): an id that belongs to another
/// container, user or agent must read as not found through a route scoped to this one, and must
/// change nothing. Today's gaps are in known-authz-violations.json under "cross-object ".
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CrossObjectAccessTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task CrossContainer_ObjectAccess_Returns404()
    {
        var http = fixture.AdminClient;
        string a = await CreateContainerAsync("authz-a");
        string b = await CreateContainerAsync("authz-b");
        try
        {
            // The same path in both containers, so a route that dropped its container check but
            // read the file through this container's storage would find a file there, and answer.
            string fileOfA = await UploadAsync(a, "same.txt", TextOfA);
            string fileOfB = await UploadAsync(b, "same.txt", TextOfB);
            await WaitUntilReadyAsync(a, fileOfA);
            await WaitUntilReadyAsync(b, fileOfB);

            // The probes below only prove something if the same calls work on the right container.
            (await http.GetStringAsync($"/api/containers/{a}/files/{fileOfA}/content")).Should().Contain(TextOfA);
            (await http.GetStringAsync($"/api/containers/{b}/files/{fileOfB}/content")).Should().Contain(TextOfB);

            var viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
            var ownersPat = await PostAsync<PatCreateResponse>(http, "/api/v1/auth/pats", new PatCreateRequest($"authz-{Guid.NewGuid():N}"));
            var agentA = await PostAsync<AgentDto>(http, "/api/v1/agents/", new CreateAgentRequest($"authz-a-{Guid.NewGuid():N}"));
            var agentB = await PostAsync<AgentDto>(http, "/api/v1/agents/", new CreateAgentRequest($"authz-b-{Guid.NewGuid():N}"));
            var keyOfB = await PostAsync<CreateAgentKeyResponse>(http, $"/api/v1/agents/{agentB.Id}/keys", new CreateAgentKeyRequest("authz"));

            var violations = new List<string>();
            async Task OtherFileSurvivesAsync(string what)
            {
                if ((await http.GetAsync($"/api/containers/{b}/files/{fileOfB}")).StatusCode != HttpStatusCode.OK)
                    violations.Add($"cross-object {what}: removed the other container's file");
            }

            (HttpMethod Method, string Route, string Path)[] reads =
            [
                (HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}", $"/api/containers/{a}/files/{fileOfB}"),
                (HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}/content", $"/api/containers/{a}/files/{fileOfB}/content"),
                (HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}/reindex-check", $"/api/containers/{a}/files/{fileOfB}/reindex-check"),
            ];
            foreach (var (method, route, path) in reads)
            {
                using var response = await http.SendAsync(new HttpRequestMessage(method, path));
                if (response.StatusCode != HttpStatusCode.NotFound)
                    violations.Add($"cross-object {method} {route}: expects 404, got {(int)response.StatusCode}");
            }

            // MCP takes the same pair of ids. A tool that answers with either file's text through
            // the wrong container, or deletes the other container's file, leaks whatever it says.
            await using (var mcp = await CrossSurface.Surfaces.McpAsync(fixture))
            {
                string read = Text(await mcp.CallToolAsync("get_document", new Dictionary<string, object?> { ["containerId"] = a, ["fileId"] = fileOfB }));
                if (read.Contains(TextOfB, StringComparison.Ordinal) || read.Contains(TextOfA, StringComparison.Ordinal))
                    violations.Add("cross-object mcp get_document: returned a file's text through the other container");
                await mcp.CallToolAsync("delete_file", new Dictionary<string, object?> { ["containerId"] = a, ["fileId"] = fileOfB });
                await OtherFileSurvivesAsync("mcp delete_file");
                await mcp.CallToolAsync("bulk_delete", new Dictionary<string, object?> { ["containerId"] = a, ["fileIds"] = $"[\"{fileOfB}\"]" });
                await OtherFileSurvivesAsync("mcp bulk_delete");
            }

            using (var response = await http.DeleteAsync($"/api/containers/{a}/files/{fileOfB}"))
                if (response.StatusCode != HttpStatusCode.NotFound)
                    violations.Add($"cross-object DELETE /api/containers/{{containerId:guid}}/files/{{fileId}}: expects 404, got {(int)response.StatusCode}");
            await OtherFileSurvivesAsync("DELETE /api/containers/{containerId:guid}/files/{fileId}");

            using (var response = await viewer.DeleteAsync($"/api/v1/auth/pats/{ownersPat.Id}"))
                if (response.StatusCode != HttpStatusCode.NotFound)
                    violations.Add($"cross-object DELETE /api/v1/auth/pats/{{id:guid}}: expects 404, got {(int)response.StatusCode}");
            var pats = await http.GetFromJsonAsync<List<PatListItem>>("/api/v1/auth/pats");
            if (pats!.Single(p => p.Id == ownersPat.Id).IsRevoked)
                violations.Add("cross-object DELETE /api/v1/auth/pats/{id:guid}: revoked another user's token");

            using (var response = await http.DeleteAsync($"/api/v1/agents/{agentA.Id}/keys/{keyOfB.KeyId}"))
                if (response.StatusCode != HttpStatusCode.NotFound)
                    violations.Add($"cross-object DELETE /api/v1/agents/{{id:guid}}/keys/{{keyId:guid}}: expects 404, got {(int)response.StatusCode}");
            var keys = await http.GetFromJsonAsync<List<AgentKeyListItem>>($"/api/v1/agents/{agentB.Id}/keys");
            if (keys!.Single(k => k.Id == keyOfB.KeyId).IsRevoked)
                violations.Add("cross-object DELETE /api/v1/agents/{id:guid}/keys/{keyId:guid}: revoked another agent's key");

            AuthzExpectations.ShouldMatchKnown(violations, "cross-object ");
        }
        finally
        {
            await DeleteContainerAsync(a);
            await DeleteContainerAsync(b);
        }
    }

    private async Task<string> CreateContainerAsync(string prefix)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private const string TextOfA = "Container A holds the heron ledger.";
    private const string TextOfB = "Container B holds the walrus inventory.";

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));

    private async Task WaitUntilReadyAsync(string containerId, string fileId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string status = "";
        while (DateTime.UtcNow < deadline)
        {
            var file = await fixture.AdminClient.GetFromJsonAsync<JsonElement>($"/api/containers/{containerId}/files/{fileId}");
            status = file.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("Status", out var value) ? value.GetString() ?? "" : "";
            if (status is "Ready" or "Failed")
                break;
            await Task.Delay(250);
        }
        status.Should().Be("Ready", "both files must be ingested before their content routes can answer");
    }

    private async Task<string> UploadAsync(string containerId, string fileName, string text)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", fileName);
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("documents")[0].GetProperty("documentId").GetString()!;
    }

    private async Task DeleteContainerAsync(string containerId)
    {
        using var listed = await fixture.AdminClient.GetAsync($"/api/containers/{containerId}/files");
        if (!listed.IsSuccessStatusCode)
            return;
        var files = await listed.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var file in Items(files))
            if (file.TryGetProperty("id", out var id))
                await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{id.GetString()}");
        await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
    }

    private static IEnumerable<JsonElement> Items(JsonElement files) => files.ValueKind switch
    {
        JsonValueKind.Array => files.EnumerateArray(),
        JsonValueKind.Object when files.TryGetProperty("items", out var items) => items.EnumerateArray(),
        _ => [],
    };

    private static async Task<T> PostAsync<T>(HttpClient http, string path, object body)
    {
        var response = await http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
