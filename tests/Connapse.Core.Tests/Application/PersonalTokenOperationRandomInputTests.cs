using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Tokens;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The personal access token operations' rules over random inputs (CON-72): only a person signed in
/// themselves lists, makes or revokes a token, and only their own; a refusal changes, audits and answers
/// nothing; and a token is made only with a name, scopes a personal token can hold, and an expiry still to
/// come. Every sample builds its own fakes, because CsCheck runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class PersonalTokenOperationRandomInputTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // Names as a caller might send them: valid, padded, at and past the limit, blank, or missing.
    private static readonly Gen<string?> RequestedName = Gen.OneOfConst<string?>(
        "script", " my script ", "x", new string('n', 256), new string('n', 257), " " + new string('n', 256) + " ", "", "   ", null);

    // Scopes as a caller might send them: none, a personal token's, repeated, or ones it can't hold.
    private static readonly Gen<string[]?> RequestedScopes = Gen.OneOfConst<string[]?>(
        null, [], ["knowledge:read"], ["knowledge:read", "admin:users", "admin:settings"], ["knowledge:read", "knowledge:read"],
        ["admin:manage-admins"], ["knowledge:write", "agent:ingest"], ["Knowledge:Read"], [""], [null!, "admin:users"], ["knowledge:read,admin:users"]);

    // Expiries as a caller might send them: none, past, now, future, and future with no time zone.
    private static readonly Gen<DateTime?> RequestedExpiry = Gen.OneOfConst<DateTime?>(
        null, Now.AddDays(-1), Now, Now.AddSeconds(1), Now.AddDays(30), DateTime.SpecifyKind(Now.AddDays(30), DateTimeKind.Unspecified));

    private static readonly string[] PersonalTokenScopes =
        ["knowledge:read", "knowledge:write", "admin:users", "admin:audit", "admin:oauth-clients", "admin:settings", "admin:sources"];

    /// <summary>One sample's world: whether the caller signed in themselves, and the fakes.</summary>
    private sealed class World
    {
        public readonly IPatService Pats = Substitute.For<IPatService>();
        public readonly PolicyAuthorizer Authorizer;
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(Now));
        public readonly Guid CallerId = Guid.NewGuid();
        public readonly OperationContext Caller;

        public World(bool person, OperationSurface surface)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.Person && person);
            Caller = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, CallerId.ToString())], "test")), surface);
        }

        public int AuditCalls => Audit.ReceivedCalls().Count();

        public int PatCalls => Pats.ReceivedCalls().Count();
    }

    [Fact]
    [RandomInputTestOf(typeof(ListPersonalTokens))]
    public Task List_AnyCaller_ListsOnlyAPersonsOwnTokens() =>
        Gen.Select(Gen.Bool, Gen.Enum<OperationSurface>())
            .SampleAsync(async t =>
            {
                var (person, surface) = t;
                var world = new World(person, surface);
                world.Pats.ListAsync(world.CallerId, Arg.Any<CancellationToken>())
                    .Returns([new PatListItem(Guid.NewGuid(), "script", "cnp_abcdefgh", [], Now, null, null, false)]);

                var listed = await new ListPersonalTokens(world.Pats, world.Authorizer).RunAsync(world.Caller);

                listed.Status.Should().Be(person ? PatStatus.Success : PatStatus.Forbidden);
                world.PatCalls.Should().Be(person ? 1 : 0);
                if (person)
                    await world.Pats.Received(1).ListAsync(world.CallerId, Arg.Any<CancellationToken>());
                else
                    listed.Result.Should().BeNull();
            });

    [Fact]
    [RandomInputTestOf(typeof(CreatePersonalToken))]
    public Task Create_AnyRequest_FollowsTheRules_AndStoresWhatWasChecked() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Enum<OperationSurface>()), RequestedName, RequestedScopes, RequestedExpiry, Gen.Bool)
            .SampleAsync(async t =>
            {
                var ((person, surface), name, scopes, expiry, approval) = t;
                var world = new World(person, surface);
                world.Pats.CreateAsync(world.CallerId, Arg.Any<PatCreateRequest>(), Arg.Any<CancellationToken>())
                    .Returns(c => new PatCreateResponse(Guid.NewGuid(), c.ArgAt<PatCreateRequest>(1).Name, "cnp_secret",
                        c.ArgAt<PatCreateRequest>(1).Scopes ?? [], Now, c.ArgAt<PatCreateRequest>(1).ExpiresAt));

                var created = await new CreatePersonalToken(world.Pats, world.Authorizer, world.Audit, world.Clock)
                    .RunAsync(world.Caller, new PatCreateRequest(name!, scopes, expiry, approval));

                string trimmed = name?.Trim() ?? "";
                bool validName = trimmed.Length is >= 1 and <= 256;
                // The deprecated agent:ingest is dropped, not refused (CON-171).
                string?[] kept = (scopes ?? []).Where(s => s != "agent:ingest").ToArray();
                bool validScopes = kept.All(s => s is not null && PersonalTokenScopes.Contains(s, StringComparer.Ordinal));
                bool validExpiry = expiry is null || DateTime.SpecifyKind(expiry.Value, DateTimeKind.Utc) > Now;
                PatStatus expected = !person ? PatStatus.Forbidden : validName && validScopes && validExpiry ? PatStatus.Success : PatStatus.Invalid;
                created.Status.Should().Be(expected);

                if (expected == PatStatus.Success)
                {
                    string[] stored = kept.Distinct(StringComparer.Ordinal).ToArray()!;
                    await world.Pats.Received(1).CreateAsync(world.CallerId,
                        Arg.Is<PatCreateRequest>(r => r.Name == trimmed && r.Scopes!.SequenceEqual(stored) && r.RequiresApproval == approval
                            && (expiry == null ? r.ExpiresAt == null : r.ExpiresAt!.Value.Kind == DateTimeKind.Utc && r.ExpiresAt > Now)),
                        Arg.Any<CancellationToken>());
                }
                else
                {
                    world.PatCalls.Should().Be(0);
                    created.Result.Should().BeNull();
                }
                world.AuditCalls.Should().Be(expected == PatStatus.Success ? 1 : 0);
            });

    [Fact]
    [RandomInputTestOf(typeof(RevokePersonalToken))]
    public Task Revoke_AnyToken_RevokesOnlyAPersonsOwn_AndAuditsOnlyARevocation() =>
        Gen.Select(Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (person, surface, exists) = t;
                var world = new World(person, surface);
                Guid id = Guid.NewGuid();
                world.Pats.RevokeAsync(world.CallerId, id, Arg.Any<CancellationToken>()).Returns(exists);

                var revoked = await new RevokePersonalToken(world.Pats, world.Authorizer, world.Audit).RunAsync(world.Caller, id);

                PatStatus expected = !person ? PatStatus.Forbidden : exists ? PatStatus.Success : PatStatus.NotFound;
                revoked.Status.Should().Be(expected);
                await world.Pats.Received(person ? 1 : 0).RevokeAsync(world.CallerId, id, Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == PatStatus.Success ? 1 : 0);
                if (expected == PatStatus.Success)
                    await world.Audit.Received(1).LogAsync(RevokePersonalToken.RevokedEvent, "pat", id.ToString(), null, Arg.Any<CancellationToken>());
            });

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
