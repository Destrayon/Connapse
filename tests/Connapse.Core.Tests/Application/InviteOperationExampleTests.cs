using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Invites;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// One example per branch of the invite operations (CON-70), each pinning the exact status, message,
/// field and effects. The random-input tests cover the rules broadly; these pin every answer's words.
/// </summary>
[Trait("Category", "Unit")]
public class InviteOperationExampleTests
{
    private const string NoPermission = "You don't have permission to manage users.";

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IInviteStore _store = Substitute.For<IInviteStore>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly Guid _callerId = Guid.NewGuid();

    private OperationContext Caller(bool admin = true, bool scope = true, bool signedIn = true)
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.AdminUsersScope).Returns(scope);
        Claim[] claims = signedIn ? [new Claim(ClaimTypes.NameIdentifier, _callerId.ToString())] : [];
        return new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), OperationSurface.Rest);
    }

    // ── invites.list ────────────────────────────────────────────────────────────────────────────

    private Task<Attempt<InviteList, InviteStatus>> ListAsync(ListInvitesQuery query, int total = 0, bool admin = true, bool scope = true)
    {
        var context = Caller(admin, scope);
        _store.ListPendingAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(new InvitePage([], total));
        return new ListInvites(_store, _authorizer).RunAsync(context, query);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task List_WithoutAdminOrScope_IsForbidden(bool admin, bool scope)
    {
        var listed = await ListAsync(new ListInvitesQuery(), admin: admin, scope: scope);

        listed.Status.Should().Be(InviteStatus.Forbidden);
        listed.Detail.Should().Be(NoPermission);
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, null, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfBounds_IsInvalidPage(int skip, int? take, string detail)
    {
        var listed = await ListAsync(new ListInvitesQuery(skip, take));

        listed.Status.Should().Be(InviteStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 1, 5, true)]
    [InlineData(0, 200, 5, false)]
    [InlineData(0, 2, 2, false)]
    [InlineData(1, 2, 4, true)]
    [InlineData(0, null, 5, false)]
    public async Task List_PageAtTheBounds_ListsAndSaysWhetherMoreFollow(int skip, int? take, int total, bool hasMore)
    {
        var listed = await ListAsync(new ListInvitesQuery(skip, take), total);

        listed.Status.Should().Be(InviteStatus.Success);
        listed.Result!.TotalCount.Should().Be(total);
        listed.Result.HasMore.Should().Be(hasMore);
        await _store.Received(1).ListPendingAsync(skip, take, Arg.Any<CancellationToken>());
    }

    // ── invites.create ──────────────────────────────────────────────────────────────────────────

    private Task<Attempt<CreatedInvite, InviteStatus>> CreateAsync(OperationContext context, string? email, string? role) =>
        new CreateInvite(_store, _authorizer, _audit).RunAsync(context, new CreateInviteRequest(email, role));

    private void PolicyAllows(bool allows) =>
        _store.MayInviteAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(allows);

    private void StoreAnswers(InviteWrite outcome) =>
        _store.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => outcome == InviteWrite.Done
                ? new InviteCreation(outcome, new Invite(Guid.NewGuid(), c.ArgAt<string>(0), c.ArgAt<string>(1), null, Epoch, Epoch.AddDays(7)), "the-token")
                : new InviteCreation(outcome, null, null));

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Create_WithoutAdminScopeOrAnId_IsForbiddenAndAsksNothing(bool admin, bool scope, bool signedIn)
    {
        var created = await CreateAsync(Caller(admin, scope, signedIn), "a@example.com", "Viewer");

        created.Status.Should().Be(InviteStatus.Forbidden);
        created.Detail.Should().Be(NoPermission);
        created.Result.Should().BeNull();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "Email is required.")]
    [InlineData("   ", "Email is required.")]
    [InlineData("no-domain", "'no-domain' is an invalid email address.")]
    [InlineData("Name <c@example.com>", "'Name <c@example.com>' is an invalid email address.")]
    public async Task Create_AnEmailThatIsntOne_IsInvalidInvite(string? email, string detail)
    {
        var created = await CreateAsync(Caller(), email, "Viewer");

        created.Status.Should().Be(InviteStatus.InvalidInvite);
        created.Detail.Should().Be(detail);
        created.Field.Should().Be("email");
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_AnEmailOfExactlyTheLimit_IsAccepted_AndOneLongerIsNot()
    {
        PolicyAllows(true);
        StoreAnswers(InviteWrite.Done);
        string atLimit = new string('a', 256 - "@example.com".Length) + "@example.com";

        (await CreateAsync(Caller(), atLimit, "Viewer")).Status.Should().Be(InviteStatus.Success);
        var tooLong = await CreateAsync(Caller(), "a" + atLimit, "Viewer");

        tooLong.Status.Should().Be(InviteStatus.InvalidInvite);
        tooLong.Detail.Should().Be("Email must be at most 256 characters.");
        tooLong.Field.Should().Be("email");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("Agent")]
    public async Task Create_AReservedRole_IsInvalidInvite(string role)
    {
        var created = await CreateAsync(Caller(), "a@example.com", role);

        created.Status.Should().Be(InviteStatus.InvalidInvite);
        created.Detail.Should().Be($"The '{role}' role is not allowed in an invitation.");
        created.Field.Should().Be("role");
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Nope", "'Nope' is an invalid role. Roles: Viewer, Editor, Admin.")]
    [InlineData(null, "'' is an invalid role. Roles: Viewer, Editor, Admin.")]
    public async Task Create_AnUnknownRole_IsInvalidInvite(string? role, string detail)
    {
        var created = await CreateAsync(Caller(), "a@example.com", role);

        created.Status.Should().Be(InviteStatus.InvalidInvite);
        created.Detail.Should().Be(detail);
        created.Field.Should().Be("role");
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ThePolicyRefuses_IsForbiddenAndMakesNothing()
    {
        PolicyAllows(false);

        var created = await CreateAsync(Caller(), "a@example.com", "admin");

        created.Status.Should().Be(InviteStatus.Forbidden);
        created.Detail.Should().Be("Only the Owner can invite users with the Admin role.");
        await _store.Received(1).MayInviteAsync(_callerId, "Admin", Arg.Any<CancellationToken>());
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default, default);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(InviteWrite.UserExists, InviteStatus.UserExists, "A user with email 'a@example.com' already exists.")]
    [InlineData(InviteWrite.PendingExists, InviteStatus.InvitePending, "A pending invitation for 'a@example.com' already exists.")]
    public async Task Create_TheStoreFindsTheAddressTaken_IsAConflictWithoutAToken(InviteWrite outcome, InviteStatus status, string detail)
    {
        PolicyAllows(true);
        StoreAnswers(outcome);

        var created = await CreateAsync(Caller(), " a@example.com ", "Viewer");

        created.Status.Should().Be(status);
        created.Detail.Should().Be(detail);
        created.Field.Should().Be("email");
        created.Result.Should().BeNull();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(" viewer ", "Viewer")]
    [InlineData("EDITOR", "Editor")]
    [InlineData("admin", "Admin")]
    public async Task Create_Made_AnswersTheTokenOnceAndAuditsTheRoleOnly(string requested, string stored)
    {
        PolicyAllows(true);
        StoreAnswers(InviteWrite.Done);

        var created = await CreateAsync(Caller(), " a@example.com ", requested);

        created.Status.Should().Be(InviteStatus.Success);
        created.Result!.Token.Should().Be("the-token");
        created.Result.Invite.Role.Should().Be(stored);
        await _store.Received(1).CreateAsync("a@example.com", stored, _callerId, Arg.Any<CancellationToken>());
        var logged = _audit.ReceivedCalls().Single().GetArguments();
        logged[0].Should().Be(CreateInvite.CreatedEvent);
        logged[1].Should().Be("invite");
        logged[2].Should().Be(created.Result.Invite.Id.ToString());
        logged[3].Should().BeEquivalentTo(new { Role = stored });
        System.Text.Json.JsonSerializer.Serialize(logged[3]).Should().NotContain("the-token");
        CreateInvite.CreatedEvent.Should().Be("invite.created");
    }

    // ── invites.revoke ──────────────────────────────────────────────────────────────────────────

    private Task<Attempt<Guid, InviteStatus>> RevokeAsync(OperationContext context, Guid id) =>
        new RevokeInvite(_store, _authorizer, _audit).RunAsync(context, id);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Revoke_WithoutAdminOrScope_IsForbiddenAndRevokesNothing(bool admin, bool scope)
    {
        var revoked = await RevokeAsync(Caller(admin, scope), Guid.NewGuid());

        revoked.Status.Should().Be(InviteStatus.Forbidden);
        revoked.Detail.Should().Be(NoPermission);
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_NoSuchInvite_IsNotFoundAndUnaudited()
    {
        var revoked = await RevokeAsync(Caller(), Guid.NewGuid());

        revoked.Status.Should().Be(InviteStatus.NotFound);
        revoked.Detail.Should().Be("Invite not found");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_Revoked_Audits()
    {
        Guid id = Guid.NewGuid();
        _store.RevokeAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var revoked = await RevokeAsync(Caller(), id);

        revoked.Status.Should().Be(InviteStatus.Success);
        revoked.Result.Should().Be(id);
        await _audit.Received(1).LogAsync(RevokeInvite.RevokedEvent, "invite", id.ToString(), null, Arg.Any<CancellationToken>());
        RevokeInvite.RevokedEvent.Should().Be("invite.revoked");
    }
}
