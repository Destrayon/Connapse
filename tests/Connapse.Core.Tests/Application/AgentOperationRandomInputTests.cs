using System.Security.Claims;
using System.Text.RegularExpressions;
using Connapse.Application;
using Connapse.Application.Agents;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// The agent operations' rules over random inputs (CON-71): only an administrator with admin:users lists,
/// reads, makes, changes or deletes an agent or its keys; a refusal changes, audits and answers nothing;
/// and a token is answered only by a key that was made. Every sample builds its own fakes, because CsCheck
/// runs samples on several threads.
/// </summary>
[Trait("Category", "Unit")]
public class AgentOperationRandomInputTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Names as a caller might send them: valid, padded, too short or long, blank, missing, or with spaces or symbols.
    private static readonly Gen<string?> RequestedName = Gen.OneOfConst<string?>(
        "bot", " bot_2 ", "a", "ab", new string('n', 64), new string('n', 65), "", "   ", null, "has space", "dots.not.ok", "émoji");

    private static readonly Gen<string?> RequestedDescription = Gen.OneOfConst<string?>(
        null, "", "  ", "does things", " padded ", new string('d', 500), new string('d', 501));

    // Scopes as a caller might send them: none, an agent key's, repeated, or ones only a person's token holds.
    private static readonly Gen<string[]?> RequestedScopes = Gen.OneOfConst<string[]?>(
        null, [], ["knowledge:read"], ["knowledge:read", "knowledge:write", "agent:ingest"], ["knowledge:read", "knowledge:read"],
        ["admin:users"], ["knowledge:read", "admin:settings"], ["Knowledge:Read"], [""], [null!, "admin:users"], ["knowledge:read,admin:users"]);

    private static readonly string[] AgentKeyScopes = ["knowledge:read", "knowledge:write", "agent:ingest"];

    /// <summary>One sample's world: who the caller is and what they hold, and the fakes.</summary>
    private sealed class World
    {
        public readonly IAgentService Agents = Substitute.For<IAgentService>();
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

        public int AgentCalls => Agents.ReceivedCalls().Count();
    }

    private static AgentDto Agent(Guid id, string name = "bot", params AgentKeyListItem[] keys) =>
        new(id, name, null, true, Guid.NewGuid(), Epoch, keys);

    [Fact]
    [RandomInputTestOf(typeof(ListAgents))]
    public Task List_AnyCaller_ListsOnlyForAnAdminWithTheScope() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>())
            .SampleAsync(async t =>
            {
                var (admin, scope, surface) = t;
                var world = new World(admin, scope, surface);
                world.Agents.ListAsync(Arg.Any<CancellationToken>()).Returns([Agent(Guid.NewGuid())]);

                var listed = await new ListAgents(world.Agents, world.Authorizer).RunAsync(world.Caller);

                bool allowed = admin && scope;
                listed.Status.Should().Be(allowed ? AgentStatus.Success : AgentStatus.Forbidden);
                world.AgentCalls.Should().Be(allowed ? 1 : 0);
                if (allowed)
                    listed.Result.Should().ContainSingle();
            });

    [Fact]
    [RandomInputTestOf(typeof(GetAgent))]
    public Task Get_AnyAgent_ReadsOnlyForAnAdminWithTheScope_AndTellsAMissingOneApart() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                world.Agents.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? Agent(id) : null);

                var got = await new GetAgent(world.Agents, world.Authorizer).RunAsync(world.Caller, id);

                got.Status.Should().Be(!admin || !scope ? AgentStatus.Forbidden : exists ? AgentStatus.Success : AgentStatus.NotFound);
                world.AgentCalls.Should().Be(admin && scope ? 1 : 0);
                if (got.Succeeded)
                    got.Result!.Id.Should().Be(id);
            });

    [Fact]
    [RandomInputTestOf(typeof(CreateAgent))]
    public Task Create_AnyRequest_FollowsTheRules_AndStoresWhatWasChecked() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>()), RequestedName, RequestedDescription, Gen.Bool)
            .SampleAsync(async t =>
            {
                var ((admin, scope, surface), name, description, taken) = t;
                var world = new World(admin, scope, surface);
                world.Agents.CreateAsync(Arg.Any<CreateAgentRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(c => taken ? null : Agent(Guid.NewGuid(), c.ArgAt<CreateAgentRequest>(0).Name));

                var created = await new CreateAgent(world.Agents, world.Authorizer, world.Audit)
                    .RunAsync(world.Caller, new CreateAgentRequest(name!, description));

                string trimmed = name?.Trim() ?? "";
                bool validName = trimmed.Length is >= 2 and <= 64 && Regex.IsMatch(trimmed, "^[a-zA-Z0-9_-]+$");
                string? storedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
                bool validDescription = storedDescription is null || storedDescription.Length <= 500;
                AgentStatus expected =
                    !admin || !scope ? AgentStatus.Forbidden
                    : !validName || !validDescription ? AgentStatus.InvalidAgent
                    : taken ? AgentStatus.NameTaken
                    : AgentStatus.Success;
                created.Status.Should().Be(expected);

                bool wellFormed = admin && scope && validName && validDescription;
                if (wellFormed)
                    await world.Agents.Received(1).CreateAsync(new CreateAgentRequest(trimmed, storedDescription), world.CallerId, Arg.Any<CancellationToken>());
                else
                    world.AgentCalls.Should().Be(0);
                world.AuditCalls.Should().Be(expected == AgentStatus.Success ? 1 : 0);
                if (expected != AgentStatus.Success)
                    created.Result.Should().BeNull();
            });

    [Fact]
    [RandomInputTestOf(typeof(SetAgentActive))]
    public Task SetActive_AnyAgent_ChangesOnlyForAnAdminWithTheScope_AndAuditsWhatChanged() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool, Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists, active) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                world.Agents.SetActiveAsync(id, active, Arg.Any<CancellationToken>()).Returns(exists);

                var set = await new SetAgentActive(world.Agents, world.Authorizer, world.Audit).RunAsync(world.Caller, new SetAgentActiveCommand(id, active));

                AgentStatus expected = !admin || !scope ? AgentStatus.Forbidden : exists ? AgentStatus.Success : AgentStatus.NotFound;
                set.Status.Should().Be(expected);
                await world.Agents.Received(expected == AgentStatus.Forbidden ? 0 : 1).SetActiveAsync(id, active, Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == AgentStatus.Success ? 1 : 0);
                if (expected == AgentStatus.Success)
                    await world.Audit.Received(1).LogAsync(active ? SetAgentActive.EnabledEvent : SetAgentActive.DisabledEvent,
                        "agent", id.ToString(), null, Arg.Any<CancellationToken>());
            });

    [Fact]
    [RandomInputTestOf(typeof(DeleteAgent))]
    public Task Delete_AnyAgent_DeletesOnlyForAnAdminWithTheScope_AndAuditsOnlyADeletion() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                world.Agents.DeleteAsync(id, Arg.Any<CancellationToken>()).Returns(exists);

                var deleted = await new DeleteAgent(world.Agents, world.Authorizer, world.Audit).RunAsync(world.Caller, id);

                AgentStatus expected = !admin || !scope ? AgentStatus.Forbidden : exists ? AgentStatus.Success : AgentStatus.NotFound;
                deleted.Status.Should().Be(expected);
                await world.Agents.Received(expected == AgentStatus.Forbidden ? 0 : 1).DeleteAsync(id, Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == AgentStatus.Success ? 1 : 0);
            });

    [Fact]
    [RandomInputTestOf(typeof(ListAgentKeys))]
    public Task ListKeys_AnyAgent_ListsOnlyForAnAdminWithTheScope_AndTellsAMissingAgentFromOneWithNoKeys() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool, Gen.Int[0, 3])
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists, keyCount) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                var keys = Enumerable.Range(0, keyCount)
                    .Select(i => new AgentKeyListItem(Guid.NewGuid(), $"k{i}", "cnp_", [], Epoch, null, null, i % 2 == 1)).ToArray();
                world.Agents.GetAsync(id, Arg.Any<CancellationToken>()).Returns(exists ? Agent(id, "bot", keys) : null);

                var listed = await new ListAgentKeys(world.Agents, world.Authorizer).RunAsync(world.Caller, id);

                listed.Status.Should().Be(!admin || !scope ? AgentStatus.Forbidden : exists ? AgentStatus.Success : AgentStatus.NotFound);
                world.AgentCalls.Should().Be(admin && scope ? 1 : 0);
                if (listed.Succeeded)
                    listed.Result.Should().Equal(keys);
            });

    [Fact]
    [RandomInputTestOf(typeof(CreateAgentKey))]
    public Task CreateKey_AnyRequest_FollowsTheRules_AndAnswersATokenOnlyForAKeyMade() =>
        Gen.Select(Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>()), RequestedName, RequestedScopes, Gen.Bool)
            .SampleAsync(async t =>
            {
                var ((admin, scope, surface), name, scopes, exists) = t;
                var world = new World(admin, scope, surface);
                Guid id = Guid.NewGuid();
                world.Agents.CreateKeyAsync(id, Arg.Any<CreateAgentKeyRequest>(), Arg.Any<CancellationToken>())
                    .Returns(c => exists
                        ? new CreateAgentKeyResponse(Guid.NewGuid(), id.ToString(), "the-token", c.ArgAt<CreateAgentKeyRequest>(1).Scopes!, Epoch, null)
                        : null);

                var created = await new CreateAgentKey(world.Agents, world.Authorizer, world.Audit)
                    .RunAsync(world.Caller, new CreateAgentKeyCommand(id, new CreateAgentKeyRequest(name!, scopes)));

                string trimmed = name?.Trim() ?? "";
                bool validName = trimmed.Length is >= 2 and <= 64;
                string[] distinct = (scopes ?? []).Distinct().ToArray();
                bool validScopes = distinct.All(AgentKeyScopes.Contains);
                AgentStatus expected =
                    !admin || !scope ? AgentStatus.Forbidden
                    : !validName || !validScopes ? AgentStatus.InvalidKey
                    : exists ? AgentStatus.Success
                    : AgentStatus.NotFound;
                created.Status.Should().Be(expected);

                if (admin && scope && validName && validScopes)
                    await world.Agents.Received(1).CreateKeyAsync(id,
                        Arg.Is<CreateAgentKeyRequest>(r => r.Name == trimmed && r.Scopes!.SequenceEqual(distinct)), Arg.Any<CancellationToken>());
                else
                    world.AgentCalls.Should().Be(0);
                world.AuditCalls.Should().Be(expected == AgentStatus.Success ? 1 : 0);
                if (expected == AgentStatus.Success)
                    created.Result!.Token.Should().Be("the-token");
                else
                    created.Result.Should().BeNull("a refusal answers no token");
            });

    [Fact]
    [RandomInputTestOf(typeof(RevokeAgentKey))]
    public Task RevokeKey_AnyKey_RevokesOnlyForAnAdminWithTheScope_AndAuditsOnlyARevocation() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Enum<OperationSurface>(), Gen.Bool)
            .SampleAsync(async t =>
            {
                var (admin, scope, surface, exists) = t;
                var world = new World(admin, scope, surface);
                Guid agentId = Guid.NewGuid(), keyId = Guid.NewGuid();
                world.Agents.RevokeKeyAsync(agentId, keyId, Arg.Any<CancellationToken>()).Returns(exists);

                var revoked = await new RevokeAgentKey(world.Agents, world.Authorizer, world.Audit)
                    .RunAsync(world.Caller, new AgentKeyTarget(agentId, keyId));

                AgentStatus expected = !admin || !scope ? AgentStatus.Forbidden : exists ? AgentStatus.Success : AgentStatus.KeyNotFound;
                revoked.Status.Should().Be(expected);
                await world.Agents.Received(expected == AgentStatus.Forbidden ? 0 : 1).RevokeKeyAsync(agentId, keyId, Arg.Any<CancellationToken>());
                world.AuditCalls.Should().Be(expected == AgentStatus.Success ? 1 : 0);
            });
}
