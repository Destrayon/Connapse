using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Identity.Data.Entities;
using FluentAssertions;

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
    public void ParseKind_KnownNames_ParseToKind(string name, DeletionItemKind expected)
    {
        DeletionTargets.ParseKind(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("container")]
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
    public async Task Request_Anonymous_IsRefused()
    {
        RequestDeletions request = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System);

        var result = await request.ExecuteAsync(new OperationContext(new ClaimsPrincipal(new ClaimsIdentity()), OperationSurface.Mcp),
            [new DeletionRequestItem("file", "c", "/a.md", "old")]);

        result.Status.Should().Be(DeletionBatchOutcome.NotSignedIn);
    }

    [Theory]
    [InlineData(OperationSurface.Mcp)]
    [InlineData(OperationSurface.Rest)]
    public async Task Decide_OutsideUi_IsRefused(OperationSurface surface)
    {
        DecideDeletionBatch decide = new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, TimeProvider.System, null!);
        ClaimsPrincipal owner = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));

        var result = await decide.ExecuteAsync(new OperationContext(owner, surface), Guid.NewGuid(), approve: true);

        result.Status.Should().Be(DeletionBatchOutcome.NotFromUi);
    }
}
