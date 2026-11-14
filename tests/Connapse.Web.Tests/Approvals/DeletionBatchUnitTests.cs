using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Web.Tests.Approvals;

/// <summary>The pure rules of deletion batches (CON-315): kinds, fingerprints, and what the agent is told.</summary>
[Trait("Category", "Unit")]
public class DeletionBatchUnitTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    private static Document Doc(string id = "d1", string path = "/notes/a.md", long size = 10, DateTime? created = null) =>
        new(id, Guid.Empty.ToString(), "a.md", "text/markdown", path, size, created ?? Now, []);

    [Theory]
    [InlineData("file", DeletionItemKind.File)]
    [InlineData(" Folder ", DeletionItemKind.Folder)]
    [InlineData("memory_space", DeletionItemKind.MemorySpace)]
    [InlineData("memory-space", DeletionItemKind.MemorySpace)]
    [InlineData("container", DeletionItemKind.Container)]
    [InlineData("fact", DeletionItemKind.Fact)]
    [InlineData("memory_fact", DeletionItemKind.Fact)]
    public void ParseKind_KnownNames_ParseToKind(string name, DeletionItemKind expected)
    {
        DeletionTargets.ParseKind(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("document")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseKind_OtherNames_AreRejected(string? name)
    {
        DeletionTargets.ParseKind(name).Should().BeNull();
    }

    [Fact]
    public void FileFingerprint_SameFile_IsStable()
    {
        DeletionTargets.FileFingerprint(Doc()).Should().Be(DeletionTargets.FileFingerprint(Doc()));
    }

    [Fact]
    public void FileFingerprint_FileReplacedOrMoved_Changes()
    {
        string original = DeletionTargets.FileFingerprint(Doc());

        DeletionTargets.FileFingerprint(Doc(size: 11)).Should().NotBe(original);
        DeletionTargets.FileFingerprint(Doc(path: "/notes/b.md")).Should().NotBe(original);
        DeletionTargets.FileFingerprint(Doc(created: Now.AddSeconds(1))).Should().NotBe(original);
    }

    [Fact]
    public void FolderFingerprint_DocumentOrder_DoesNotMatter()
    {
        Guid container = Guid.NewGuid();
        Document[] docs = [Doc("a"), Doc("b", "/notes/b.md")];

        DeletionTargets.FolderFingerprint(container, "/notes/", true, docs)
            .Should().Be(DeletionTargets.FolderFingerprint(container, "/notes/", true, docs.Reverse()));
    }

    [Fact]
    public void FolderFingerprint_FileAddedToFolder_Changes()
    {
        Guid container = Guid.NewGuid();
        string before = DeletionTargets.FolderFingerprint(container, "/notes/", true, [Doc("a")]);

        DeletionTargets.FolderFingerprint(container, "/notes/", true, [Doc("a"), Doc("b", "/notes/b.md")]).Should().NotBe(before);
    }

    private static DeletionBatchEntity Batch(DeletionBatchStatus status, DateTime expires, params DeletionItemState[] states)
    {
        DeletionBatchEntity batch = new()
        {
            Id = Guid.NewGuid(),
            TokenKey = "pat:x",
            OwnerUserId = Guid.NewGuid(),
            Status = status,
            CreatedAt = Now.AddHours(-1),
            ExpiresAt = expires,
        };
        for (int i = 0; i < states.Length; i++)
            batch.Items.Add(new DeletionBatchItemEntity { Id = Guid.NewGuid(), Position = i, Kind = DeletionItemKind.File, State = states[i] });
        return batch;
    }

    [Fact]
    public void View_PendingPastExpiry_ReadsExpired()
    {
        var view = DeletionTargets.View(Batch(DeletionBatchStatus.Pending, Now.AddMinutes(-1), DeletionItemState.Pending), Now);

        view.State.Should().Be("expired");
        view.Items.Single().State.Should().Be("expired");
        view.Message.Should().Contain("nothing was deleted");
    }

    [Fact]
    public void View_Done_CountsEachOutcome()
    {
        var view = DeletionTargets.View(Batch(DeletionBatchStatus.Done, Now.AddDays(1),
            DeletionItemState.Deleted, DeletionItemState.Deleted, DeletionItemState.Skipped, DeletionItemState.NotApproved), Now);

        view.State.Should().Be("done");
        view.Message.Should().Contain("2 deleted").And.Contain("1 skipped").And.Contain("1 not approved");
        view.Items.Select(i => i.State).Should().Equal("deleted", "deleted", "skipped", "not_approved");
    }

    [Fact]
    public void View_IsYours_OnlyForOwner()
    {
        var batch = Batch(DeletionBatchStatus.Pending, Now.AddDays(1), DeletionItemState.Pending);

        DeletionTargets.View(batch, Now, batch.OwnerUserId).IsYours.Should().BeTrue();
        DeletionTargets.View(batch, Now, Guid.NewGuid()).IsYours.Should().BeFalse();
    }

    [Fact]
    public void View_IsYours_ForTheTokenThatAsked()
    {
        var batch = Batch(DeletionBatchStatus.Pending, Now.AddDays(1), DeletionItemState.Pending);

        DeletionTargets.View(batch, Now, viewerToken: batch.TokenKey).IsYours.Should().BeTrue();
        DeletionTargets.View(batch, Now, viewerToken: "pat:other").IsYours.Should().BeFalse();
        DeletionTargets.View(batch, Now).IsYours.Should().BeFalse();
    }

    [Fact]
    public async Task Status_TokenReadingItsOwnBatch_SeesItAsItsOwn()
    {
        var batch = Batch(DeletionBatchStatus.Done, Now.AddDays(1), DeletionItemState.Deleted);
        var store = Substitute.For<IDeletionBatchStore>();
        store.GetForTokenAsync(batch.Id, batch.TokenKey, Arg.Any<CancellationToken>()).Returns(batch);
        ClaimsPrincipal token = new(new ClaimsIdentity([new Claim("pat_id", "x"), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var result = await new GetDeletionBatchStatus(store, TimeProvider.System).ExecuteAsync(new OperationContext(token, OperationSurface.Mcp), batch.Id);

        result.Succeeded.Should().BeTrue();
        result.Result!.IsYours.Should().BeTrue();
    }

    [Fact]
    public async Task Request_Anonymous_IsRefused()
    {
        RequestDeletions request = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, null!);

        var result = await request.ExecuteAsync(new OperationContext(new ClaimsPrincipal(new ClaimsIdentity()), OperationSurface.Mcp),
            [new DeletionRequestItem("file", "c", "/a.md", "old")]);

        result.Status.Should().Be(DeletionBatchOutcome.NotSignedIn);
    }

    [Theory]
    [InlineData(OperationSurface.Mcp)]
    [InlineData(OperationSurface.Rest)]
    public async Task Decide_OutsideUi_IsRefused(OperationSurface surface)
    {
        DecideDeletionBatch decide = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, null!, null!, null!, null!);
        ClaimsPrincipal owner = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var result = await decide.ExecuteAsync(new OperationContext(owner, surface), Guid.NewGuid(), approve: true);

        result.Status.Should().Be(DeletionBatchOutcome.NotFromUi);
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void HeldDelete_EachDeleteTool_BecomesItsItems()
    {
        McpApprovalGate.DeletionItems("delete_file", null, Args(new { containerId = "c", fileId = "f" }))!.Value.Items
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = "file", Container = "c", Target = "f" });
        McpApprovalGate.DeletionItems("bulk_delete", null, Args(new { containerId = "c", fileIds = "[\"a\",\"b\"]" }))!.Value.Items!
            .Select(i => i.Target).Should().Equal("a", "b");
        McpApprovalGate.DeletionItems("container_delete", null, Args(new { containerId = "c" }))!.Value.Items
            .Should().ContainSingle().Which.Kind.Should().Be("container");
        McpApprovalGate.DeletionItems("memory_forget", null, Args(new { containerId = "s", factId = "x" }))!.Value.Items
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = "fact", Target = "x" });
        McpApprovalGate.DeletionItems(McpApiTools.WriteName, "folders.delete",
                Args(new { operation = "folders.delete", parameters = new { containerId = "c", path = "/old/" } }))!.Value.Items
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new { Kind = "folder", Container = "c", Target = "/old/" });
    }

    [Fact]
    public void HeldDelete_BulkDeleteWithoutAnArray_NamesTheProblem()
    {
        McpApprovalGate.DeletionItems("bulk_delete", null, Args(new { containerId = "c", fileIds = "a,b" }))!.Value.Problem
            .Should().Contain("JSON array");
    }

    [Fact]
    public void HeldDelete_DeleteFileWithFileIds_BecomesOneItemPerFile()
    {
        McpApprovalGate.DeletionItems("delete_file", null, Args(new { containerId = "c", fileIds = "[\"a\",\"b\"]" }))!.Value.Items!
            .Select(i => (i.Container, i.Target)).Should().Equal(("c", "a"), ("c", "b"));
        McpApprovalGate.DeletionItems("delete_file", null, Args(new { containerId = "c", fileId = "f", fileIds = "" }))!.Value.Items
            .Should().ContainSingle().Which.Target.Should().Be("f");
    }

    [Fact]
    public void HeldDelete_DeleteFileWithFileIdAndFileIds_NamesTheProblem()
    {
        var both = McpApprovalGate.DeletionItems("delete_file", null, Args(new { containerId = "c", fileId = "f", fileIds = "[\"a\"]" }))!.Value;
        both.Items.Should().BeNull();
        both.Problem.Should().Be("Provide either fileId or fileIds, not both.");
        McpApprovalGate.DeletionItems("delete_file", null, Args(new { containerId = "c", fileIds = "a,b" }))!.Value.Problem
            .Should().Contain("JSON array");
    }

    [Theory]
    [InlineData(McpApiTools.WriteName, "connections.create")]
    [InlineData(McpApiTools.WriteName, "containers.settings.update")]
    [InlineData("upload_file", null)]
    public void HeldDelete_ACallThatIsNotAKnowledgeDeletion_KeepsTheRepeatApproval(string tool, string? operation)
    {
        McpApprovalGate.DeletionItems(tool, operation, Args(new { containerId = "c" })).Should().BeNull();
    }
}
