using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Tokens;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// One example per branch of the personal access token operations (CON-72), each pinning the exact status,
/// message, field and effects. The random-input tests cover the rules broadly; these pin every answer's words.
/// </summary>
[Trait("Category", "Unit")]
public class PersonalTokenOperationExampleTests
{
    private const string NoPermission =
        "You don't have permission to manage personal access tokens with this sign-in: sign in yourself, not with a token or agent key.";

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IPatService _pats = Substitute.For<IPatService>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly FixedClock _time = new(new DateTimeOffset(Now));
    private readonly Guid _callerId = Guid.NewGuid();
    private readonly Guid _tokenId = Guid.NewGuid();

    private OperationContext Caller(bool person = true, bool signedIn = true)
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Person).Returns(person);
        Claim[] claims = signedIn ? [new Claim(ClaimTypes.NameIdentifier, _callerId.ToString())] : [];
        return new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), OperationSurface.Rest);
    }

    private CreatePersonalToken Create() => new(_pats, _authorizer, _audit, _time);

    private object?[] AuditCall() => _audit.ReceivedCalls().Single().GetArguments();

    private void Minting() =>
        _pats.CreateAsync(_callerId, Arg.Any<PatCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => new PatCreateResponse(_tokenId, c.ArgAt<PatCreateRequest>(1).Name, "cnp_secret",
                c.ArgAt<PatCreateRequest>(1).Scopes ?? [], Now, c.ArgAt<PatCreateRequest>(1).ExpiresAt));

    // ── who may ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EveryOperation_WithoutAPersonSignedIn_IsForbiddenAndTouchesNothing(bool person, bool signedIn)
    {
        var context = Caller(person, signedIn);

        var attempts = new[]
        {
            (await new ListPersonalTokens(_pats, _authorizer).RunAsync(context)).Status,
            (await Create().RunAsync(context, new("script"))).Status,
            (await new RevokePersonalToken(_pats, _authorizer, _audit).RunAsync(context, _tokenId)).Status,
        };

        attempts.Should().AllBeEquivalentTo(PatStatus.Forbidden);
        _pats.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("client_id", "an-mcp-client")]
    [InlineData("auth_method", "api_key")]
    [InlineData("auth_method", "agent_api_key")]
    public async Task EveryOperation_FromAToken_IsForbidden_EvenWhenThePersonPolicyLetsItThrough(string type, string value)
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Person).Returns(true);
        var context = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _callerId.ToString()), new Claim(type, value)], "test")), OperationSurface.Rest);

        var attempts = new[]
        {
            (await new ListPersonalTokens(_pats, _authorizer).RunAsync(context)).Status,
            (await Create().RunAsync(context, new("script"))).Status,
            (await new RevokePersonalToken(_pats, _authorizer, _audit).RunAsync(context, _tokenId)).Status,
        };

        attempts.Should().AllBeEquivalentTo(PatStatus.Forbidden);
        _pats.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Forbidden_SaysWhy()
    {
        var listed = await new ListPersonalTokens(_pats, _authorizer).RunAsync(Caller(person: false));

        listed.Detail.Should().Be(NoPermission);
    }

    // ── list ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ListsTheCallersOwnTokens()
    {
        PatListItem token = new(_tokenId, "script", "cnp_abcdefgh", ["knowledge:read"], Now, null, null, false);
        _pats.ListAsync(_callerId, Arg.Any<CancellationToken>()).Returns([token]);

        var listed = await new ListPersonalTokens(_pats, _authorizer).RunAsync(Caller());

        listed.Result.Should().Equal(token);
    }

    // ── create ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_NoName_IsInvalid(string? name)
    {
        var created = await Create().RunAsync(Caller(), new(name!));

        created.Status.Should().Be(PatStatus.Invalid);
        created.Detail.Should().Be("Token name is required.");
        created.Field.Should().Be("name");
        _pats.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ALongName_IsInvalid()
    {
        var created = await Create().RunAsync(Caller(), new(new string('n', 257)));

        created.Status.Should().Be(PatStatus.Invalid);
        created.Detail.Should().Be("Token name must be at most 256 characters.");
        created.Field.Should().Be("name");
    }

    [Theory]
    [InlineData("admin:manage-admins")]
    [InlineData("Knowledge:Read")]
    [InlineData("")]
    public async Task Create_AScopeAPersonalTokenCantHold_IsInvalid(string scope)
    {
        var created = await Create().RunAsync(Caller(), new("script", ["knowledge:read", scope]));

        created.Status.Should().Be(PatStatus.Invalid);
        created.Detail.Should().Be($"'{scope}' is an invalid personal access token scope. Scopes: knowledge:read, knowledge:write, admin:users, admin:audit, admin:oauth-clients, admin:settings, admin:sources.");
        created.Field.Should().Be("scopes");
        _pats.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ANullScopeBeforeAForbiddenOne_IsInvalid()
    {
        var created = await Create().RunAsync(Caller(), new("script", [null!, "admin:manage-admins"]));

        created.Status.Should().Be(PatStatus.Invalid);
        created.Field.Should().Be("scopes");
        _pats.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task Create_AnExpiryThatIsntInTheFuture_IsInvalid(int secondsFromNow)
    {
        var created = await Create().RunAsync(Caller(), new("script", ExpiresAt: Now.AddSeconds(secondsFromNow)));

        created.Status.Should().Be(PatStatus.Invalid);
        created.Detail.Should().Be("Token expiry must be in the future.");
        created.Field.Should().Be("expiresAt");
        _pats.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_AnExpiryWithNoTimeZone_IsReadAsUtc()
    {
        Minting();
        DateTime unspecified = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Unspecified);

        await Create().RunAsync(Caller(), new("script", ExpiresAt: unspecified));

        await _pats.Received(1).CreateAsync(_callerId,
            Arg.Is<PatCreateRequest>(r => r.ExpiresAt == unspecified && r.ExpiresAt!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_StoresWhatWasChecked_AndAuditsTheNameAndScopes_NeverTheToken()
    {
        Minting();

        var created = await Create().RunAsync(Caller(),
            new(" script ", ["knowledge:read", "knowledge:read", "admin:users"], Now.AddDays(1), RequiresApproval: false));

        created.Result!.Token.Should().Be("cnp_secret");
        await _pats.Received(1).CreateAsync(_callerId,
            Arg.Is<PatCreateRequest>(r => r.Name == "script" && r.Scopes!.SequenceEqual(new[] { "knowledge:read", "admin:users" })
                && r.ExpiresAt == Now.AddDays(1) && !r.RequiresApproval),
            Arg.Any<CancellationToken>());
        AuditCall()[0].Should().Be("pat.created");
        AuditCall()[1].Should().Be("pat");
        AuditCall()[2].Should().Be(_tokenId.ToString());
        AuditCall()[3].Should().BeEquivalentTo(new { Name = "script", Scopes = new[] { "knowledge:read", "admin:users" } });
    }

    [Fact]
    public async Task Create_NoScopes_MakesATokenThatHoldsNone()
    {
        Minting();

        var created = await Create().RunAsync(Caller(), new("script"));

        created.Succeeded.Should().BeTrue();
        await _pats.Received(1).CreateAsync(_callerId, Arg.Is<PatCreateRequest>(r => r.Scopes!.Length == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_TheDeprecatedAgentIngestScope_IsDroppedNotRefused()
    {
        // No check ever required agent:ingest (CON-171); a client that still asks for it gets its token, without it.
        Minting();

        var created = await Create().RunAsync(Caller(), new("script", ["agent:ingest", "knowledge:write"]));

        created.Succeeded.Should().BeTrue();
        created.Result!.Scopes.Should().Equal("knowledge:write");
        await _pats.Received(1).CreateAsync(_callerId,
            Arg.Is<PatCreateRequest>(r => r.Scopes!.SequenceEqual(new[] { "knowledge:write" })), Arg.Any<CancellationToken>());
    }

    // ── revoke ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_AuditsTheRevocation_AndAMissingTokenIsNotFound()
    {
        _pats.RevokeAsync(_callerId, _tokenId, Arg.Any<CancellationToken>()).Returns(true);

        var revoked = await new RevokePersonalToken(_pats, _authorizer, _audit).RunAsync(Caller(), _tokenId);
        var missing = await new RevokePersonalToken(_pats, _authorizer, _audit).RunAsync(Caller(), Guid.NewGuid());

        revoked.Result.Should().Be(_tokenId);
        AuditCall()[0].Should().Be("pat.revoked");
        AuditCall()[2].Should().Be(_tokenId.ToString());
        missing.Status.Should().Be(PatStatus.NotFound);
        missing.Detail.Should().Be("Token not found or already revoked.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
