using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Invites;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The invite operations' rules over random inputs (CON-70): only an administrator with admin:users
/// invites, lists or revokes; a refusal makes, audits and answers nothing; and a token is answered only
/// by an invite that was made. Every sample builds its own fakes, because CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class InviteOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Assignable = ["Viewer", "Editor", "Admin"];

    // Addresses as a caller might send them: valid, padded, blank, missing a domain, or with a display name.
    private static readonly Gen<string?> RequestedEmail = Gen.OneOfConst<string?>(
        "a@example.com", " b@example.com ", "", "   ", null, "no-domain", "Name <c@example.com>", "d@", new string('e', 250) + "@x.io");

    // Roles as a caller might send them: assignable in any case and padded, reserved, unknown, blank.
    private static readonly Gen<string?> RequestedRole = Gen.OneOfConst<string?>("Viewer", "editor", "ADMIN", " Viewer ", "Owner", "agent", "Nope", "", null);

    /// <summary>One sample's world: who the caller is and what they hold, and the fakes.</summary>
    private sealed class World
    {
        public readonly IInviteStore Store = Substitute.For<IInviteStore>();
        public readonly PolicyAuthorizer Authorizer;
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly Guid CallerId = Guid.NewGuid();
        public readonly OperationContext Caller;

        public World(bool admin, bool scope, OperationSurface surface)
        {
            Authorizer = new PolicyAuthorizer(policy => policy switch { Policies.Admin => admin, Policies.AdminUsersScope => scope, _ => false });
            Caller = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, CallerId.ToString())], "test")), surface);
        }

        public int AuditCalls => Audit.ReceivedCalls().Count();
    }

    [Fact]
    [RandomInputTestOf(typeof(ListInvites))]
    public Task List_AnyQuery_RefusesWithoutAdminAndScope_AndPagesWithinBounds() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Int[-3, 3], Gen.Int[-2, 205].Nullable(), Gen.Int[0, 300])
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, skip, take, total) = t;
                var world = new World(admin, scope, surface);
                world.Store.ListPendingAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new InvitePage([], total));

                var listed = await new ListInvites(world.Store, world.Authorizer).RunAsync(world.Caller, new ListInvitesQuery(skip, take));

                bool validPage = skip >= 0 && take is null or (>= 1 and <= ListInvites.MaxTake);
                listed.Status.Should().Be(!admin || !scope ? InviteStatus.Forbidden : validPage ? InviteStatus.Success : InviteStatus.InvalidPage);
                if (listed.Status != InviteStatus.Success)
                {
                    world.Store.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                await world.Store.Received(1).ListPendingAsync(skip, take, Arg.Any<CancellationToken>());
                listed.Result!.TotalCount.Should().Be(total);
                listed.Result.HasMore.Should().Be(take is int pageSize && skip + pageSize < total);
            });

    [Fact]
    [RandomInputTestOf(typeof(CreateInvite))]
    public Task Create_AnyRequest_FollowsTheRules_AndAnswersATokenOnlyForAnInviteMade() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>()), RequestedEmail, RequestedRole, Gen.Enum<InviteWrite>())
            .SampleAsync(async t =>
            {
                var ((admin, scope, mayInvite, surface), email, role, store) = t;
                var world = new World(admin, scope, surface);
                world.Store.MayInviteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(mayInvite);
                world.Store.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(c => store == InviteWrite.Done
                        ? new InviteCreation(store, new Invite(Guid.NewGuid(), c.ArgAt<string>(0), c.ArgAt<string>(1), null, Epoch, Epoch.AddDays(7)), "the-token")
                        : new InviteCreation(store, null, null));

                var created = await new CreateInvite(world.Store, world.Authorizer, world.Audit)
                    .RunAsync(world.Caller, new CreateInviteRequest(email, role));

                string trimmed = email?.Trim() ?? "";
                bool validEmail = trimmed.Length is > 0 and <= 256 && System.Net.Mail.MailAddress.TryCreate(trimmed, out var address) && address.Address == trimmed;
                string? canonical = Assignable.FirstOrDefault(a => a.Equals(role?.Trim(), StringComparison.OrdinalIgnoreCase));
                InviteStatus expected =
                    !admin || !scope ? InviteStatus.Forbidden
                    : !validEmail || canonical is null ? InviteStatus.InvalidInvite
                    : !mayInvite || store == InviteWrite.Forbidden ? InviteStatus.Forbidden
                    : store == InviteWrite.UserExists ? InviteStatus.UserExists
                    : store == InviteWrite.PendingExists ? InviteStatus.InvitePending
                    : InviteStatus.Success;
                created.Status.Should().Be(expected);

                // The policy is asked only once the request is well formed, and always with the role's own name;
                // the store is asked to make only what the policy allowed.
                bool wellFormed = admin && scope && validEmail && canonical is not null;
                if (wellFormed)
                    await world.Store.Received(1).MayInviteAsync(world.CallerId, canonical!, Arg.Any<CancellationToken>());
                else
                    await world.Store.DidNotReceiveWithAnyArgs().MayInviteAsync(default, default!, default);
                if (wellFormed && mayInvite)
                    await world.Store.Received(1).CreateAsync(trimmed, canonical!, world.CallerId, Arg.Any<CancellationToken>());
                else
                    await world.Store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default, default);
                world.AuditCalls.Should().Be(expected == InviteStatus.Success ? 1 : 0);
                if (expected == InviteStatus.Success)
                {
                    created.Result!.Token.Should().Be("the-token");
                    created.Result.Invite.Role.Should().Be(canonical);
                }
                else
                    created.Result.Should().BeNull("a refusal answers no token");
            });

    [Fact]
    [RandomInputTestOf(typeof(RevokeInvite))]
    public Task Revoke_AnyInvite_RefusesWithoutAdminAndScope_AndAuditsOnlyARevocation() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                world.Store.RevokeAsync(id, Arg.Any<CancellationToken>()).Returns(exists);

                var revoked = await new RevokeInvite(world.Store, world.Authorizer, world.Audit).RunAsync(world.Caller, id);

                InviteStatus expected = !admin || !scope ? InviteStatus.Forbidden : exists ? InviteStatus.Success : InviteStatus.NotFound;
                revoked.Status.Should().Be(expected);
                await world.Store.Received(expected == InviteStatus.Forbidden ? 0 : 1).RevokeAsync(id, Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == InviteStatus.Success ? 1 : 0);
            });
}
