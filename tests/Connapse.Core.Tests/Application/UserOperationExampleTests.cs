using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// One example per branch of the user operations (CON-14), each pinning the exact status, message and
/// effects. The random-input tests cover the rules broadly but rarely reach the deepest branches.
/// </summary>
[Trait("Category", "Unit")]
public class UserOperationExampleTests
{
    private const string NoPermission = "You don't have permission to manage users.";
    private const string ChangedMeanwhile = "The user changed while this was being checked. Load it again and retry.";

    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly ICustomLinkRemovalStore _removals = Substitute.For<ICustomLinkRemovalStore>();
    private readonly Guid _callerId = Guid.NewGuid();

    private OperationContext Caller(bool admin = true, bool scope = true, bool owner = false)
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.AdminUsersScope).Returns(scope);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Owner).Returns(owner);
        return new OperationContext(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _callerId.ToString())], "test")), OperationSurface.Rest);
    }

    private UserAccount Existing(params string[] roles) => Existing(Guid.NewGuid(), roles);

    private UserAccount Existing(Guid id, params string[] roles)
    {
        var user = new UserAccount(id, "target@example.com", null, roles, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);
        _directory.GetAsync(id, Arg.Any<CancellationToken>()).Returns(user);
        return user;
    }

    // ── users.list ──────────────────────────────────────────────────────────────────────────────

    private Task<Attempt<UserList, UserStatus>> ListAsync(ListUsersQuery query, int total = 0, bool admin = true, bool scope = true)
    {
        var context = Caller(admin, scope);
        _directory.ListAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new DirectoryPage([], total));
        return new ListUsers(_directory, _authorizer).RunAsync(context, query);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task List_WithoutAdminOrScope_IsForbidden(bool admin, bool scope)
    {
        var listed = await ListAsync(new ListUsersQuery(), admin: admin, scope: scope);

        listed.Status.Should().Be(UserStatus.Forbidden);
        listed.Detail.Should().Be(NoPermission);
    }

    [Theory]
    [InlineData(-1, null, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfBounds_IsInvalidPage(int skip, int? take, string detail)
    {
        var listed = await ListAsync(new ListUsersQuery(skip, take));

        listed.Status.Should().Be(UserStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
        _directory.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 1, 5, true)]
    [InlineData(0, 200, 5, false)]
    [InlineData(0, 2, 2, false)]
    [InlineData(1, 2, 4, true)]
    [InlineData(0, null, 5, false)]
    public async Task List_PageAtTheBounds_ListsAndSaysWhetherMoreFollow(int skip, int? take, int total, bool hasMore)
    {
        var listed = await ListAsync(new ListUsersQuery(skip, take), total);

        listed.Status.Should().Be(UserStatus.Success);
        listed.Result!.TotalCount.Should().Be(total);
        listed.Result.HasMore.Should().Be(hasMore);
        await _directory.Received(1).ListAsync(skip, take, Arg.Any<CancellationToken>());
    }

    // ── users.setRoles ──────────────────────────────────────────────────────────────────────────

    private Task<Attempt<UserAccount, UserStatus>> SetRolesAsync(OperationContext context, Guid id, IReadOnlyList<string> roles) =>
        new SetUserRoles(_directory, _authorizer, _audit).RunAsync(context, new SetUserRolesRequest(id, roles));

    private void StoreAnswers(DirectoryWrite outcome, params string[] errors) =>
        _directory.SetRolesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RoleChange(outcome, errors));

    [Fact]
    public async Task SetRoles_WithoutScope_IsForbidden()
    {
        var set = await SetRolesAsync(Caller(scope: false), Existing("Viewer").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.Forbidden);
        set.Detail.Should().Be(NoPermission);
    }

    [Fact]
    public async Task SetRoles_NoSuchUser_IsNotFound()
    {
        var set = await SetRolesAsync(Caller(), Guid.NewGuid(), ["Editor"]);

        set.Status.Should().Be(UserStatus.NotFound);
        set.Detail.Should().Be("User not found");
    }

    [Fact]
    public async Task SetRoles_OwnAccount_IsForbidden()
    {
        var context = Caller(owner: true);

        var set = await SetRolesAsync(context, Existing(_callerId, "Viewer").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.Forbidden);
        set.Detail.Should().Be("You can't change your own account here.");
    }

    [Fact]
    public async Task SetRoles_OwnersAccount_IsForbidden()
    {
        var set = await SetRolesAsync(Caller(owner: true), Existing("Owner").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.Forbidden);
        set.Detail.Should().Be("The Owner's account can't be changed here.");
    }

    [Fact]
    public async Task SetRoles_AnAdminsAccountByANonOwner_IsForbidden()
    {
        var set = await SetRolesAsync(Caller(), Existing("Admin").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.Forbidden);
        set.Detail.Should().Be("Only the Owner can manage Admins.");
    }

    [Fact]
    public async Task SetRoles_NoRolesAtAll_IsInvalidRoles()
    {
        var set = await SetRolesAsync(Caller(), Existing("Viewer").Id, null!);

        set.Status.Should().Be(UserStatus.InvalidRoles);
        set.Detail.Should().Be("At least one role is required.");
        set.Field.Should().Be("roles");
    }

    [Fact]
    public async Task SetRoles_ANullRole_IsNamedAsAnEmptyRole()
    {
        var set = await SetRolesAsync(Caller(), Existing("Viewer").Id, [null!]);

        set.Status.Should().Be(UserStatus.InvalidRoles);
        set.Detail.Should().Be("'' is not a role. Roles: Viewer, Editor, Admin.");
        set.Field.Should().Be("roles");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Agent")]
    public async Task SetRoles_AReservedRole_IsInvalidRoles(string role)
    {
        var set = await SetRolesAsync(Caller(owner: true), Existing("Viewer").Id, ["Viewer", role]);

        set.Status.Should().Be(UserStatus.InvalidRoles);
        set.Detail.Should().Be($"The '{role}' role cannot be assigned via this endpoint.");
        set.Field.Should().Be("roles");
    }

    [Fact]
    public async Task SetRoles_AnUnknownRole_IsInvalidRoles()
    {
        var set = await SetRolesAsync(Caller(owner: true), Existing("Viewer").Id, ["Viewer", "Nope"]);

        set.Status.Should().Be(UserStatus.InvalidRoles);
        set.Detail.Should().Be("'Nope' is not a role. Roles: Viewer, Editor, Admin.");
        set.Field.Should().Be("roles");
    }

    [Fact]
    public async Task SetRoles_AdminByANonOwner_IsForbiddenAndWritesNothing()
    {
        var set = await SetRolesAsync(Caller(), Existing("Viewer").Id, ["Admin"]);

        set.Status.Should().Be(UserStatus.Forbidden);
        set.Detail.Should().Be("Only the Owner can assign the Admin role.");
        await _directory.DidNotReceiveWithAnyArgs().SetRolesAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData(false, " viewer ", "Viewer")]
    [InlineData(false, "EDITOR", "Editor")]
    [InlineData(true, "admin", "Admin")]
    public async Task SetRoles_AnAssignableRole_IsStoredUnderItsDefinedNameAndAudited(bool owner, string requested, string stored)
    {
        var target = Existing("Viewer");
        StoreAnswers(DirectoryWrite.Done);

        var set = await SetRolesAsync(Caller(owner: owner), target.Id, [requested]);

        set.Status.Should().Be(UserStatus.Success);
        set.Result!.Roles.Should().Equal(stored);
        await _directory.Received(1).SetRolesAsync(
            target.Id, Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "Viewer" })),
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { stored })), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(SetUserRoles.UpdatedEvent, "user", target.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
        SetUserRoles.UpdatedEvent.Should().Be("user.roles.updated");
    }

    [Fact]
    public async Task SetRoles_TheUserChangedMeanwhile_IsChangedAndUnaudited()
    {
        StoreAnswers(DirectoryWrite.Changed);

        var set = await SetRolesAsync(Caller(), Existing("Viewer").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.Changed);
        set.Detail.Should().Be(ChangedMeanwhile);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SetRoles_IdentityRefuses_IsNotChangedWithItsReasons()
    {
        StoreAnswers(DirectoryWrite.Failed, "First reason.", "Second reason.");

        var set = await SetRolesAsync(Caller(), Existing("Viewer").Id, ["Editor"]);

        set.Status.Should().Be(UserStatus.NotChanged);
        set.Detail.Should().Be("First reason. Second reason.");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    // ── users.delete ────────────────────────────────────────────────────────────────────────────

    private Task<Attempt<UserAccount, UserStatus>> DeleteAsync(OperationContext context, Guid id) =>
        new DeleteUser(_directory, _removals, _authorizer, _audit).RunAsync(context, id);

    [Fact]
    public async Task Delete_WithoutScope_IsForbiddenAndQueuesNothing()
    {
        var deleted = await DeleteAsync(Caller(scope: false), Existing("Viewer").Id);

        deleted.Status.Should().Be(UserStatus.Forbidden);
        deleted.Detail.Should().Be(NoPermission);
        _removals.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_NoSuchUser_IsNotFound()
    {
        var deleted = await DeleteAsync(Caller(), Guid.NewGuid());

        deleted.Status.Should().Be(UserStatus.NotFound);
        deleted.Detail.Should().Be("User not found");
    }

    [Fact]
    public async Task Delete_AnAdminByANonOwner_IsForbidden()
    {
        var deleted = await DeleteAsync(Caller(), Existing("Admin").Id);

        deleted.Status.Should().Be(UserStatus.Forbidden);
        deleted.Detail.Should().Be("Only the Owner can manage Admins.");
        _removals.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(DirectoryWrite.Changed, UserStatus.Changed, ChangedMeanwhile)]
    [InlineData(DirectoryWrite.Failed, UserStatus.NotChanged, "Failed to delete user.")]
    public async Task Delete_NotDeletedWhileTheUserStillExists_CancelsTheUnlinks(DirectoryWrite outcome, UserStatus status, string detail)
    {
        var target = Existing("Viewer");
        _directory.DeleteAsync(target.Id, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(outcome);

        var deleted = await DeleteAsync(Caller(), target.Id);

        deleted.Status.Should().Be(status);
        deleted.Detail.Should().Be(detail);
        await _removals.Received(1).EnqueueForUserAsync(target.Id, Arg.Any<CancellationToken>());
        await _removals.Received(1).CancelForUserAsync(target.Id, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _removals.DidNotReceiveWithAnyArgs().ActivateForUserAsync(default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_Deleted_ReleasesTheUnlinksAndAudits()
    {
        var target = Existing("Editor");
        _directory.DeleteAsync(target.Id, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(DirectoryWrite.Done);

        var deleted = await DeleteAsync(Caller(), target.Id);

        deleted.Status.Should().Be(UserStatus.Success);
        deleted.Result.Should().Be(target);
        await _directory.Received(1).DeleteAsync(target.Id, Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "Editor" })), Arg.Any<CancellationToken>());
        await _removals.Received(1).ActivateForUserAsync(target.Id, Arg.Any<CancellationToken>());
        await _removals.DidNotReceiveWithAnyArgs().CancelForUserAsync(default, default, default);
        await _audit.Received(1).LogAsync(DeleteUser.DeletedEvent, "user", target.Id.ToString(), null, Arg.Any<CancellationToken>());
        DeleteUser.DeletedEvent.Should().Be("user.deleted");
    }
}
