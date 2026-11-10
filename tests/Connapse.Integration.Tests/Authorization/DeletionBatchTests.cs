using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Identity.Authentication;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// CON-315: an agent asks for many deletions in one call, the owner approves all, some or none at once, Connapse
/// deletes the approved items itself, and anything gone or changed since the request is skipped and reported.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeletionBatchTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task RequestDeletions_TenItemsApprovedAtOnce_DeletesEachAndReportsEachOutcome()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync();
        string container = await CreateContainerAsync(http, "batch-ten", kind: null);
        string space = await CreateContainerAsync(http, "batch-ten-space", kind: "memory");
        try
        {
            List<string> files = [];
            for (int i = 0; i < 8; i++)
                files.Add(await UploadAsync(http, container, $"stale-{i}.txt"));
            List<string> inFolder = [await UploadAsync(http, container, "a.txt", "/old/"), await UploadAsync(http, container, "b.txt", "/old/")];

            var items = files.Select(f => new { kind = "file", container, target = f, reason = "Duplicate of the 2026 notes" })
                .Append(new { kind = "folder", container, target = "/old/", reason = "Superseded drafts" })
                .Append(new { kind = "memory_space", container = space, target = "", reason = "Scratch space from last week" })
                .ToArray();

            // One agent call asks for all ten.
            var asked = Json(await agent.CallToolAsync("request_deletions", new Dictionary<string, object?> { ["items"] = items }));
            asked.GetProperty("state").GetString().Should().Be("pending");
            asked.GetProperty("items").GetArrayLength().Should().Be(10);
            Guid batchId = asked.GetProperty("id").GetGuid();

            // The owner sees one batch, with a reason on every item.
            var listed = await ListAsync(Owner());
            var batch = listed.Should().ContainSingle(b => b.Id == batchId).Subject;
            batch.IsYours.Should().BeTrue();
            batch.Items.Should().HaveCount(10).And.OnlyContain(i => i.Reason.Length > 0);

            // One click approves them all, and Connapse deletes them.
            var decided = await DecideAsync(Owner(), batchId, approve: true);
            decided.Succeeded.Should().BeTrue(decided.Detail);
            decided.Result!.State.Should().Be("done");

            var status = Json(await agent.CallToolAsync("deletion_status", new Dictionary<string, object?> { ["batchId"] = batchId.ToString() }));
            status.GetProperty("state").GetString().Should().Be("done");
            status.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("state").GetString()).Should().AllBe("deleted");
            foreach (string file in files.Concat(inFolder))
                (await http.GetAsync($"/api/containers/{container}/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await http.GetAsync($"/api/containers/{space}")).IsSuccessStatusCode.Should().BeFalse("the memory space was deleted");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
            await http.DeleteAsync($"/api/containers/{space}");
        }
    }

    [Fact]
    public async Task Decide_ApproveSelected_DeletesOnlyTheTickedItems()
    {
        // Asked over the web API with the owner's own sign-in: REST writes take an editor, not an agent key.
        var http = fixture.AdminClient;
        var rest = http;
        string container = await CreateContainerAsync(http, "batch-some", kind: null);
        try
        {
            string keep = await UploadAsync(http, container, "keep.txt");
            string drop1 = await UploadAsync(http, container, "drop-1.txt");
            string drop2 = await UploadAsync(http, container, "drop-2.txt");

            var response = await rest.PostAsJsonAsync("/api/approvals/deletions", new
            {
                items = new[] { keep, drop1, drop2 }.Select(f => new { kind = "file", container, target = f, reason = "Old export" }),
            });
            response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
            var asked = await response.Content.ReadFromJsonAsync<JsonElement>();
            Guid batchId = asked.GetProperty("id").GetGuid();
            var ids = asked.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("target").GetString()!, i => i.GetProperty("id").GetGuid());

            var decided = await DecideAsync(Owner(), batchId, approve: true, [ids[drop1], ids[drop2]]);
            decided.Succeeded.Should().BeTrue(decided.Detail);

            var status = await (await rest.GetAsync($"/api/approvals/deletions/{batchId}")).Content.ReadFromJsonAsync<JsonElement>();
            var states = status.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("target").GetString()!, i => i.GetProperty("state").GetString());
            states.Should().Equal(new Dictionary<string, string?> { [keep] = "not_approved", [drop1] = "deleted", [drop2] = "deleted" });
            (await http.GetAsync($"/api/containers/{container}/files/{keep}")).IsSuccessStatusCode.Should().BeTrue("an unticked item stays");
            (await http.GetAsync($"/api/containers/{container}/files/{drop1}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task Decide_ItemsGoneOrChangedSinceTheRequest_AreSkippedAndReported()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync();
        string container = await CreateContainerAsync(http, "batch-changed", kind: null);
        try
        {
            string gone = await UploadAsync(http, container, "gone.txt");
            string stays = await UploadAsync(http, container, "plain.txt");
            await UploadAsync(http, container, "first.txt", "/drafts/");

            var asked = Json(await agent.CallToolAsync("request_deletions", new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new { kind = "file", container, target = gone, reason = "Unused" },
                    new { kind = "file", container, target = stays, reason = "Unused" },
                    new { kind = "folder", container, target = "/drafts/", reason = "Old drafts" },
                },
            }));
            Guid batchId = asked.GetProperty("id").GetGuid();

            // While the batch waits, one file is deleted another way and someone adds a file to the folder.
            (await http.DeleteAsync($"/api/containers/{container}/files/{gone}")).IsSuccessStatusCode.Should().BeTrue();
            string added = await UploadAsync(http, container, "new-work.txt", "/drafts/");

            var decided = await DecideAsync(Owner(), batchId, approve: true);
            decided.Succeeded.Should().BeTrue(decided.Detail);

            var items = decided.Result!.Items;
            items.Single(i => i.Target == gone).State.Should().Be("skipped");
            items.Single(i => i.Kind == "folder").State.Should().Be("skipped", "the folder changed after the request");
            items.Single(i => i.Kind == "folder").Outcome.Should().NotBeNullOrEmpty();
            items.Single(i => i.Target == stays).State.Should().Be("deleted");
            (await http.GetAsync($"/api/containers/{container}/files/{added}")).IsSuccessStatusCode.Should().BeTrue("a changed folder is left alone");
            decided.Result.Message.Should().Contain("1 deleted").And.Contain("2 skipped");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task Decide_AnyoneButTheOwner_CannotDecideAndTheOwnersDenialReachesTheAgent()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync();
        await using var stranger = await AgentAsync();
        string container = await CreateContainerAsync(http, "batch-owner", kind: null);
        try
        {
            string file = await UploadAsync(http, container, "important.txt");
            var asked = Json(await agent.CallToolAsync("request_deletions", new Dictionary<string, object?>
            {
                ["items"] = new[] { new { kind = "file", container, target = file, reason = "Looks unused" } },
            }));
            Guid batchId = asked.GetProperty("id").GetGuid();

            // Another administrator sees it but can't decide it; nor can the owner outside a browser sign-in.
            var otherAdmin = SignedIn(Guid.NewGuid());
            (await ListAsync(otherAdmin)).Single(b => b.Id == batchId).IsYours.Should().BeFalse();
            (await DecideAsync(otherAdmin, batchId, approve: true)).Status.Should().Be(DeletionBatchOutcome.NotFound);
            (await DecideAsync(Owner(OperationSurface.Rest), batchId, approve: true)).Status.Should().Be(DeletionBatchOutcome.NotFromUi);
            (await DecideAsync(Owner(authenticationType: "AuthenticationTypes.Federation"), batchId, approve: true)).Status
                .Should().Be(DeletionBatchOutcome.NotSignedIn, "a bearer token reaching the page isn't a person deciding");
            (await http.GetAsync($"/api/containers/{container}/files/{file}")).IsSuccessStatusCode.Should().BeTrue();

            // Another token can't read the batch at all.
            Text(await stranger.CallToolAsync("deletion_status", new Dictionary<string, object?> { ["batchId"] = batchId.ToString() }))
                .Should().Contain("not found").And.NotContain("important");

            (await DecideAsync(Owner(), batchId, approve: false, reason: "That file is the audit trail.")).Succeeded.Should().BeTrue();
            var denied = Json(await agent.CallToolAsync("deletion_status", new Dictionary<string, object?> { ["batchId"] = batchId.ToString() }));
            denied.GetProperty("state").GetString().Should().Be("denied");
            denied.GetProperty("message").GetString().Should().Contain("audit trail");
            (await http.GetAsync($"/api/containers/{container}/files/{file}")).IsSuccessStatusCode.Should().BeTrue("nothing is deleted on a denial");
            (await DecideAsync(Owner(), batchId, approve: true)).Status.Should().Be(DeletionBatchOutcome.NotPending, "a batch is decided once");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task RequestDeletions_AnyItemUnusable_AsksForNothingAndNamesIt()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync();
        string container = await CreateContainerAsync(http, "batch-bad", kind: null);
        try
        {
            string file = await UploadAsync(http, container, "real.txt");
            var result = await agent.CallToolAsync("request_deletions", new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new { kind = "file", container, target = file, reason = "Unused" },
                    new { kind = "file", container, target = "/missing.txt", reason = "Unused" },
                },
            });

            Text(result).Should().Contain("item 2:").And.Contain("missing.txt").And.NotContain("item 1:");
            (await ListAsync(Owner())).Should().NotContain(b => b.Items.Any(i => i.Target == file), "nothing was asked for");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    private OperationContext Owner(OperationSurface surface = OperationSurface.Ui, string authenticationType = "Identity.Application") =>
        SignedIn(AdminUserId(), surface, authenticationType);

    private static OperationContext SignedIn(Guid userId, OperationSurface surface = OperationSurface.Ui, string authenticationType = "Identity.Application") => new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Admin")], authenticationType)),
        surface);

    private async Task<IReadOnlyList<DeletionBatchView>> ListAsync(OperationContext context)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var listed = await scope.ServiceProvider.GetRequiredService<ListDeletionBatches>().ExecuteAsync(context);
        listed.Succeeded.Should().BeTrue(listed.Detail);
        return listed.Result!;
    }

    private async Task<Attempt<DeletionBatchView, DeletionBatchOutcome>> DecideAsync(
        OperationContext context, Guid batchId, bool approve, IReadOnlyCollection<Guid>? only = null, string? reason = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DecideDeletionBatch>().ExecuteAsync(context, batchId, approve, only, reason);
    }

    private HttpClient KeyClient(string key)
    {
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, key);
        return http;
    }

    private async Task<McpClient> AgentAsync() => await McpTestClient.ConnectAsync(KeyClient(await AgentKeyAsync()));

    private async Task<string> AgentKeyAsync()
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents/", new CreateAgentRequest($"batch-{Guid.NewGuid():N}"));
        var agent = (await created.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AgentDto>())!;
        var issued = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/agents/{agent.Id}/keys",
            new CreateAgentKeyRequest("batch", ["knowledge:read", "knowledge:write"], RequiresApproval: false));
        return (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreateAgentKeyResponse>())!.Token;
    }

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonElement Json(CallToolResult result)
    {
        string text = Text(result);
        result.IsError.Should().NotBe(true, text);
        text.Should().StartWith("{", text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> CreateContainerAsync(HttpClient http, string prefix, string? kind)
    {
        var response = await http.PostAsJsonAsync("/api/containers", kind is null
            ? new { name = $"{prefix}-{Guid.NewGuid():N}" }
            : (object)new { name = $"{prefix}-{Guid.NewGuid():N}", kind });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> UploadAsync(HttpClient http, string containerId, string fileName, string? folder = null)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"batch probe {fileName} {Guid.NewGuid()}"));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", fileName);
        if (folder is not null)
            multipart.Add(new StringContent(folder), "path");
        var response = await http.PostAsync($"/api/containers/{containerId}/files", multipart);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("documents")[0].GetProperty("documentId").GetString()!;
    }
}
