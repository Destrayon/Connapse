using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Users;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The user operations' rules over random inputs (CON-14): who may manage whom, which roles can be given,
/// and that a refusal changes nothing, audits nothing and unlinks nothing. Every sample builds its own
/// fakes, because CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class UserOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] AllRoles = ["Owner", "Admin", "Editor", "Viewer"];

    // Roles as a caller might send them: assignable, reserved, unknown, blank, in any case and padded.
    private static readonly Gen<string> RequestedRole = Gen.OneOfConst("Viewer", "editor", "ADMIN", " Viewer ", "Owner", "agent", "Nope", "");

    private static readonly Gen<string[]> TargetRoles = Gen.Bool.Array[AllRoles.Length].Select(mask => AllRoles.Where((_, i) => mask[i]).ToArray());

    /// <summary>One sample's world: who the caller is and what they hold, the account they act on, and the fakes.</summary>
    private sealed class World
    {
        public readonly IUserDirectory Directory = Substitute.For<IUserDirectory>();
        public readonly IOperationAuthorizer Authorizer = Substitute.For<IOperationAuthorizer>();
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly ICustomLinkRemovalStore Removals = Substitute.For<ICustomLinkRemovalStore>();
        public readonly Guid CallerId = Guid.NewGuid();
        public readonly OperationContext Caller;

        public World(bool admin, bool scope, bool owner, OperationSurface surface)
        {
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.AdminUsersScope).Returns(scope);
            Authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Owner).Returns(owner);
            Caller = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, CallerId.ToString())], "test")), surface);
        }

        public UserAccount Target(bool self, string[] roles) =>
            new(self ? CallerId : Guid.NewGuid(), "target@example.com", null, roles, Epoch, null);

        public int AuditCalls => Audit.ReceivedCalls().Count();
    }

    /// <summary>Why the operations' rules refuse a caller managing <paramref name="target"/>, or null when they may.</summary>
    private static bool MayManage(UserAccount target, Guid callerId, bool owner) =>
        target.Id != callerId
        && !target.Roles.Contains("Owner", StringComparer.OrdinalIgnoreCase)
        && (owner || !target.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase));

    // ── users.list ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListUsers))]
    public Task List_AnyQuery_RefusesWithoutAdminAndScope_AndPagesWithinBounds() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Int[-3, 3], Gen.Int[-2, 205].Nullable(), Gen.Int[0, 300])
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, skip, take, total) = t;
                var world = new World(admin, scope, owner: false, surface);
                world.Directory.ListAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                    .Returns(new DirectoryPage([], total));

                var listed = await new ListUsers(world.Directory, world.Authorizer).RunAsync(world.Caller, new ListUsersQuery(skip, take));

                if (!admin || !scope)
                {
                    listed.Status.Should().Be(UserStatus.Forbidden);
                    world.Directory.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                bool validPage = skip >= 0 && take is null or (>= 1 and <= ListUsers.MaxTake);
                listed.Status.Should().Be(validPage ? UserStatus.Success : UserStatus.InvalidPage);
                if (!validPage)
                {
                    world.Directory.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await world.Directory.Received(1).ListAsync(skip, take, Arg.Any<CancellationToken>());
                listed.Result!.TotalCount.Should().Be(total);
                listed.Result.HasMore.Should().Be(take is int pageSize && skip + pageSize < total);
            });

    // ── users.setRoles ──────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(SetUserRoles))]
    public Task SetRoles_AnyRequest_FollowsTheRules_AndARefusalChangesAndAuditsNothing() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>()),
                Gen.Bool, Gen.Bool, TargetRoles, RequestedRole.Array[0, 3], Gen.Bool)
            .SampleAsync(async t =>
            {
                var ((admin, scope, owner, surface), exists, self, targetRoles, requested, storeRefuses) = t;
                var world = new World(admin, scope, owner, surface);
                var target = world.Target(self, targetRoles);
                Guid targetId = target.Id;
                world.Directory.GetAsync(targetId, Arg.Any<CancellationToken>()).Returns(exists ? target : null);
                world.Directory.SetRolesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                    .Returns(storeRefuses ? ["Identity refused."] : []);

                var set = await new SetUserRoles(world.Directory, world.Authorizer, world.Audit)
                    .RunAsync(world.Caller, new SetUserRolesRequest(targetId, requested));

                var roles = requested.Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                bool reserved = roles.Any(r => r.Equals("Owner", StringComparison.OrdinalIgnoreCase) || r.Equals("Agent", StringComparison.OrdinalIgnoreCase));
                bool unknown = roles.Any(r => !new[] { "Viewer", "Editor", "Admin" }.Contains(r, StringComparer.OrdinalIgnoreCase));
                UserStatus expected =
                    !admin || !scope ? UserStatus.Forbidden
                    : !exists ? UserStatus.NotFound
                    : !MayManage(target, world.CallerId, owner) ? UserStatus.Forbidden
                    : roles.Count == 0 || reserved || unknown ? UserStatus.InvalidRoles
                    : !owner && roles.Contains("Admin", StringComparer.OrdinalIgnoreCase) ? UserStatus.Forbidden
                    : storeRefuses ? UserStatus.NotChanged
                    : UserStatus.Success;
                set.Status.Should().Be(expected);

                bool asked = expected is UserStatus.Success or UserStatus.NotChanged;
                await world.Directory.Received(asked ? 1 : 0).SetRolesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == UserStatus.Success ? 1 : 0);
                if (expected == UserStatus.Success)
                {
                    // Stored under the names Identity defines, whatever case and padding the caller sent.
                    set.Result!.Roles.Should().BeEquivalentTo(roles.Select(r => new[] { "Viewer", "Editor", "Admin" }.First(a => a.Equals(r, StringComparison.OrdinalIgnoreCase))));
                    await world.Directory.Received(1).SetRolesAsync(targetId, Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(set.Result.Roles)), Arg.Any<CancellationToken>());
                }
            });

    // ── users.delete ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_LosesToAnotherDeleteOfTheSameUser_LeavesTheUnlinksQueued()
    {
        // Both requests queued the user's unlinks; the other one deleted the user first, so this one's
        // delete fails. Cancelling now would drop the unlinks the winner is about to release.
        var world = new World(admin: true, scope: true, owner: true, OperationSurface.Rest);
        var target = world.Target(self: false, ["Viewer"]);
        world.Directory.GetAsync(target.Id, Arg.Any<CancellationToken>()).Returns(target, (UserAccount?)null);
        world.Directory.DeleteAsync(target.Id, Arg.Any<CancellationToken>()).Returns(false);

        await new DeleteUser(world.Directory, world.Removals, world.Authorizer, world.Audit).RunAsync(world.Caller, target.Id);

        await world.Removals.DidNotReceive().CancelForUserAsync(target.Id, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [RandomInputTestOf(typeof(DeleteUser))]
    public Task Delete_AnyTarget_FollowsTheRules_AndHoldsTheUnlinksUntilTheDeleteSucceeds() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>()), Gen.Bool, Gen.Bool, TargetRoles, Gen.Bool)
            .SampleAsync(async t =>
            {
                var ((admin, scope, owner, surface), exists, self, targetRoles, storeRefuses) = t;
                var world = new World(admin, scope, owner, surface);
                var target = world.Target(self, targetRoles);
                world.Directory.GetAsync(target.Id, Arg.Any<CancellationToken>()).Returns(exists ? target : null);
                world.Directory.DeleteAsync(target.Id, Arg.Any<CancellationToken>()).Returns(!storeRefuses);

                var deleted = await new DeleteUser(world.Directory, world.Removals, world.Authorizer, world.Audit).RunAsync(world.Caller, target.Id);

                UserStatus expected =
                    !admin || !scope ? UserStatus.Forbidden
                    : !exists ? UserStatus.NotFound
                    : !MayManage(target, world.CallerId, owner) ? UserStatus.Forbidden
                    : storeRefuses ? UserStatus.NotChanged
                    : UserStatus.Success;
                deleted.Status.Should().Be(expected);

                bool attempted = expected is UserStatus.Success or UserStatus.NotChanged;
                await world.Removals.Received(attempted ? 1 : 0).EnqueueForUserAsync(target.Id, Arg.Any<CancellationToken>());
                await world.Directory.Received(attempted ? 1 : 0).DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
                // Released only after a deletion, cancelled only after a refused one: never both, never on a refusal.
                await world.Removals.Received(expected == UserStatus.Success ? 1 : 0).ActivateForUserAsync(target.Id, Arg.Any<CancellationToken>());
                await world.Removals.Received(expected == UserStatus.NotChanged ? 1 : 0).CancelForUserAsync(target.Id, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == UserStatus.Success ? 1 : 0);
            });
}
