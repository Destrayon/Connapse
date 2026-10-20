using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;

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
            await UploadAsync(a, "a.txt");
            string fileOfB = await UploadAsync(b, "b.txt");

            var viewer = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Viewer);
            var ownersPat = await PostAsync<PatCreateResponse>(http, "/api/v1/auth/pats", new PatCreateRequest($"authz-{Guid.NewGuid():N}"));
            var agentA = await PostAsync<AgentDto>(http, "/api/v1/agents/", new CreateAgentRequest($"authz-a-{Guid.NewGuid():N}"));
            var agentB = await PostAsync<AgentDto>(http, "/api/v1/agents/", new CreateAgentRequest($"authz-b-{Guid.NewGuid():N}"));
            var keyOfB = await PostAsync<CreateAgentKeyResponse>(http, $"/api/v1/agents/{agentB.Id}/keys", new CreateAgentKeyRequest("authz"));

            (HttpClient Client, HttpMethod Method, string Route, string Path)[] cases =
            [
                (http, HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}", $"/api/containers/{a}/files/{fileOfB}"),
                (http, HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}/content", $"/api/containers/{a}/files/{fileOfB}/content"),
                (http, HttpMethod.Get, "/api/containers/{containerId:guid}/files/{fileId}/reindex-check", $"/api/containers/{a}/files/{fileOfB}/reindex-check"),
                (http, HttpMethod.Delete, "/api/containers/{containerId:guid}/files/{fileId}", $"/api/containers/{a}/files/{fileOfB}"),
                (viewer, HttpMethod.Delete, "/api/v1/auth/pats/{id:guid}", $"/api/v1/auth/pats/{ownersPat.Id}"),
                (http, HttpMethod.Delete, "/api/v1/agents/{id:guid}/keys/{keyId:guid}", $"/api/v1/agents/{agentA.Id}/keys/{keyOfB.KeyId}"),
            ];

            var violations = new List<string>();
            foreach (var (client, method, route, path) in cases)
            {
                using var response = await client.SendAsync(new HttpRequestMessage(method, path));
                if (response.StatusCode != HttpStatusCode.NotFound)
                    violations.Add($"cross-object {method} {route}: expects 404, got {(int)response.StatusCode}");
            }

            // Nothing the foreign ids named may have changed.
            if ((await http.GetAsync($"/api/containers/{b}/files/{fileOfB}")).StatusCode != HttpStatusCode.OK)
                violations.Add("cross-object DELETE /api/containers/{containerId:guid}/files/{fileId}: removed the other container's file");
            var pats = await http.GetFromJsonAsync<List<PatListItem>>("/api/v1/auth/pats");
            if (pats!.Single(p => p.Id == ownersPat.Id).IsRevoked)
                violations.Add("cross-object DELETE /api/v1/auth/pats/{id:guid}: revoked another user's token");
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

    private async Task<string> UploadAsync(string containerId, string fileName)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent("authorization matrix probe"u8.ToArray());
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
