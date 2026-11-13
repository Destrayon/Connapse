using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Core.Interfaces;
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
    public async Task Decide_FileReplacedWithSameSizeAndTypeSinceTheRequest_SkipsItAndTheNewFileSurvives()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync();
        string container = await CreateContainerAsync(http, "batch-replaced", kind: null);
        try
        {
            string replaced = await UploadAsync(http, container, "report.txt");
            string unchanged = await UploadAsync(http, container, "other.txt");

            var asked = Json(await agent.CallToolAsync("request_deletions", new Dictionary<string, object?>
            {
                ["items"] = new object[]
                {
                    new { kind = "file", container, target = replaced, reason = "Old report" },
                    new { kind = "file", container, target = unchanged, reason = "Unused" },
                },
            }));
            Guid batchId = asked.GetProperty("id").GetGuid();

            // While the batch waits, someone uploads new content to the same path: same name, same size, same type.
            var before = await (await http.GetAsync($"/api/containers/{container}/files/{replaced}")).Content.ReadFromJsonAsync<JsonElement>();
            string again = await UploadAsync(http, container, "report.txt");
            again.Should().Be(replaced, "a re-upload to the same path keeps the file's id");
            var after = await (await http.GetAsync($"/api/containers/{container}/files/{replaced}")).Content.ReadFromJsonAsync<JsonElement>();
            after.GetProperty("sizeBytes").GetInt64().Should().Be(before.GetProperty("sizeBytes").GetInt64());

            var decided = await DecideAsync(Owner(), batchId, approve: true);
            decided.Succeeded.Should().BeTrue(decided.Detail);

            var items = decided.Result!.Items;
            var skipped = items.Single(i => i.Target == replaced);
            skipped.State.Should().Be("skipped", "the file's content changed after the owner was asked");
            skipped.Outcome.Should().Contain("changed after it was requested");
            items.Single(i => i.Target == unchanged).State.Should().Be("deleted");
            (await http.GetAsync($"/api/containers/{container}/files/{replaced}")).IsSuccessStatusCode.Should().BeTrue("the new file survives");
            (await http.GetAsync($"/api/containers/{container}/files/{unchanged}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    [Fact]
    public async Task HeldDeleteFile_Approved_ConnapseDeletesItAndTheAgentReadsTheOutcome()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync(requiresApproval: true);
        string container = await CreateContainerAsync(http, "held-file", kind: null);
        try
        {
            string file = await UploadAsync(http, container, "old.txt");

            var held = HeldBatch(await agent.CallToolAsync("delete_file",
                new Dictionary<string, object?> { ["containerId"] = container, ["fileId"] = file }));
            held.GetProperty("items").EnumerateArray().Should().ContainSingle()
                .Which.GetProperty("kind").GetString().Should().Be("file");
            Guid batchId = held.GetProperty("id").GetGuid();
            (await http.GetAsync($"/api/containers/{container}/files/{file}")).IsSuccessStatusCode.Should().BeTrue("nothing is deleted before approval");

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
                (await scope.ServiceProvider.GetRequiredService<ListMcpApprovals>().ExecuteAsync(Owner())).Result!
                    .Should().NotContain(a => a.ArgumentsSummary.Contains(file), "a held delete waits as a batch, not for a repeated call");

            var waiting = agent.CallToolAsync("deletion_status",
                new Dictionary<string, object?> { ["batchId"] = batchId.ToString(), ["waitSeconds"] = 30 }).AsTask();
            (await DecideAsync(Owner(), batchId, approve: true)).Succeeded.Should().BeTrue();
            var status = Json(await waiting.WaitAsync(TimeSpan.FromSeconds(40)));

            status.GetProperty("state").GetString().Should().Be("done");
            status.GetProperty("items")[0].GetProperty("state").GetString().Should().Be("deleted");
            (await http.GetAsync($"/api/containers/{container}/files/{file}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
                "Connapse deleted the file without the agent calling again");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task HeldBulkDelete_Denied_IsOneItemPerFileAndTheAgentReadsWhy()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync(requiresApproval: true);
        string container = await CreateContainerAsync(http, "held-bulk", kind: null);
        try
        {
            string[] files = [await UploadAsync(http, container, "a.txt"), await UploadAsync(http, container, "b.txt"), await UploadAsync(http, container, "c.txt")];

            var held = HeldBatch(await agent.CallToolAsync("bulk_delete", new Dictionary<string, object?>
            {
                ["containerId"] = container,
                ["fileIds"] = JsonSerializer.Serialize(files),
            }));
            held.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("target").GetString()).Should().BeEquivalentTo(files);
            Guid batchId = held.GetProperty("id").GetGuid();

            (await DecideAsync(Owner(), batchId, approve: false, reason: "Those are this quarter's reports.")).Succeeded.Should().BeTrue();

            var status = Json(await agent.CallToolAsync("deletion_status", new Dictionary<string, object?> { ["batchId"] = batchId.ToString() }));
            status.GetProperty("state").GetString().Should().Be("denied");
            status.GetProperty("reason").GetString().Should().Be("Those are this quarter's reports.");
            foreach (string file in files)
                (await http.GetAsync($"/api/containers/{container}/files/{file}")).IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{container}");
        }
    }

    [Fact]
    public async Task HeldContainerDelete_EmptyIsDeletedAndOneThatGainedAFileIsSkipped()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync(requiresApproval: true);
        string empty = await CreateContainerAsync(http, "held-empty", kind: null);
        string filled = await CreateContainerAsync(http, "held-filled", kind: null);
        try
        {
            Guid emptyBatch = HeldBatch(await agent.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = empty }))
                .GetProperty("id").GetGuid();
            Guid filledBatch = HeldBatch(await agent.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = filled }))
                .GetProperty("id").GetGuid();

            // While the batch waits, someone adds a file to the second container.
            await UploadAsync(http, filled, "new-work.txt");

            var deleted = await DecideAsync(Owner(), emptyBatch, approve: true);
            deleted.Succeeded.Should().BeTrue(deleted.Detail);
            deleted.Result!.Items.Single().State.Should().Be("deleted");
            (await http.GetAsync($"/api/containers/{empty}")).IsSuccessStatusCode.Should().BeFalse();

            var skipped = await DecideAsync(Owner(), filledBatch, approve: true);
            skipped.Succeeded.Should().BeTrue(skipped.Detail);
            skipped.Result!.Items.Single().State.Should().Be("skipped", "the container changed after the agent asked");
            (await http.GetAsync($"/api/containers/{filled}")).IsSuccessStatusCode.Should().BeTrue();
        }
        finally
        {
            foreach (string container in new[] { empty, filled })
            {
                using var listed = await http.GetAsync($"/api/containers/{container}/files");
                if (listed.IsSuccessStatusCode && await listed.Content.ReadFromJsonAsync<JsonElement>() is { ValueKind: JsonValueKind.Array } files)
                    foreach (var file in files.EnumerateArray())
                        await http.DeleteAsync($"/api/containers/{container}/files/{file.GetProperty("id").GetString()}");
                await http.DeleteAsync($"/api/containers/{container}");
            }
        }
    }

    [Fact]
    public async Task HeldMemoryForget_Approved_ForgetsTheFact()
    {
        await using var agent = await AgentAsync(requiresApproval: true);
        string name = $"held-notes-{Guid.NewGuid():N}"[..24];
        string space = Json(await agent.CallToolAsync("memory_space_create",
            new Dictionary<string, object?> { ["name"] = name, ["kind"] = "personal" })).GetProperty("created").GetProperty("id").GetString()!;
        try
        {
            var saved = Json(await agent.CallToolAsync("memory_remember",
                new Dictionary<string, object?> { ["space"] = name, ["subject"] = "deploy", ["attribute"] = "window", ["value"] = "Fridays" }));
            string fact = saved.GetProperty("remembered").GetProperty("id").GetString()!;
            space = saved.GetProperty("savedTo").GetProperty("id").GetString()!;

            var held = HeldBatch(await agent.CallToolAsync("memory_forget",
                new Dictionary<string, object?> { ["containerId"] = space, ["factId"] = fact }));
            var item = held.GetProperty("items")[0];
            item.GetProperty("kind").GetString().Should().Be("fact");
            item.GetProperty("label").GetString().Should().Contain("Fridays", "the owner sees which fact would go");

            var decided = await DecideAsync(Owner(), held.GetProperty("id").GetGuid(), approve: true);
            decided.Succeeded.Should().BeTrue(decided.Detail);
            decided.Result!.Items.Single().State.Should().Be("deleted", decided.Result.Items.Single().Outcome);
            Text(await agent.CallToolAsync("memory_recall", new Dictionary<string, object?> { ["containerId"] = space, ["subject"] = "deploy" }))
                .Should().NotContain("Fridays");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(Guid.Parse(space));
        }
    }

    /// <summary>The batch a held delete returned: a successful answer saying it waits, followed by the batch as JSON.</summary>
    private static JsonElement HeldBatch(CallToolResult result)
    {
        string text = Text(result);
        result.IsError.Should().NotBe(true, text);
        text.Should().Contain("pending approval").And.Contain("deletion batch").And.Contain("deletion_status");
        string json = result.Content.OfType<TextContentBlock>().Select(b => b.Text).Single(t => t.StartsWith('{'));
        var batch = JsonDocument.Parse(json).RootElement.Clone();
        batch.GetProperty("state").GetString().Should().Be("pending");
        return batch;
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

    private async Task<McpClient> AgentAsync(bool requiresApproval = false) =>
        await McpTestClient.ConnectAsync(KeyClient(await AgentKeyAsync(requiresApproval)));

    private async Task<string> AgentKeyAsync(bool requiresApproval = false)
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents/", new CreateAgentRequest($"batch-{Guid.NewGuid():N}"));
        var agent = (await created.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AgentDto>())!;
        var issued = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/agents/{agent.Id}/keys",
            new CreateAgentKeyRequest("batch", ["knowledge:read", "knowledge:write"], RequiresApproval: requiresApproval));
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
