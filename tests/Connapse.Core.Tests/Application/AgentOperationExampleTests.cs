using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Agents;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// One example per branch of the agent operations (CON-71), each pinning the exact status, message,
/// field and effects. The random-input tests cover the rules broadly; these pin every answer's words.
/// </summary>
[Trait("Category", "Unit")]
public class AgentOperationExampleTests
{
    private const string NoPermission = "You don't have permission to manage agents.";

    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IAgentService _agents = Substitute.For<IAgentService>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly Guid _callerId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _keyId = Guid.NewGuid();

    private OperationContext Caller(bool admin = true, bool scope = true, bool signedIn = true)
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Admin).Returns(admin);
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.AdminUsersScope).Returns(scope);
        Claim[] claims = signedIn ? [new Claim(ClaimTypes.NameIdentifier, _callerId.ToString())] : [];
        return new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), OperationSurface.Rest);
    }

    private AgentDto Agent(string name = "bot", string? description = null) => new(_agentId, name, description, true, _callerId, Epoch, []);

    private object?[] AuditCall() => _audit.ReceivedCalls().Single().GetArguments();

    // ── who may ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task EveryOperation_WithoutAdminOrScope_IsForbiddenAndTouchesNothing(bool admin, bool scope)
    {
        var context = Caller(admin, scope);

        var attempts = new[]
        {
            (await new ListAgents(_agents, _authorizer).RunAsync(context)).Status,
            (await new GetAgent(_agents, _authorizer).RunAsync(context, _agentId)).Status,
            (await new CreateAgent(_agents, _authorizer, _audit).RunAsync(context, new("bot"))).Status,
            (await new SetAgentActive(_agents, _authorizer, _audit).RunAsync(context, new(_agentId, false))).Status,
            (await new DeleteAgent(_agents, _authorizer, _audit).RunAsync(context, _agentId)).Status,
            (await new ListAgentKeys(_agents, _authorizer).RunAsync(context, _agentId)).Status,
            (await new CreateAgentKey(_agents, _authorizer, _audit).RunAsync(context, new(_agentId, new("key")))).Status,
            (await new RevokeAgentKey(_agents, _authorizer, _audit).RunAsync(context, new(_agentId, _keyId))).Status,
        };

        attempts.Should().AllBeEquivalentTo(AgentStatus.Forbidden);
        _agents.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Forbidden_SaysWhy()
    {
        var listed = await new ListAgents(_agents, _authorizer).RunAsync(Caller(admin: false));

        listed.Detail.Should().Be(NoPermission);
    }

    [Fact]
    public async Task Create_WithoutACallerId_IsForbidden()
    {
        var created = await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(signedIn: false), new("bot"));

        created.Status.Should().Be(AgentStatus.Forbidden);
        created.Detail.Should().Be(NoPermission);
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    // ── agents ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AMissingAgent_IsNotFound()
    {
        var got = await new GetAgent(_agents, _authorizer).RunAsync(Caller(), _agentId);

        got.Status.Should().Be(AgentStatus.NotFound);
        got.Detail.Should().Be("Agent not found");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData(null)]
    public async Task Create_ABadName_IsInvalidAgent(string? name)
    {
        var created = await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(), new(name!));

        created.Status.Should().Be(AgentStatus.InvalidAgent);
        created.Detail.Should().Be("Agent name must be 2-64 characters, alphanumeric with hyphens and underscores.");
        created.Field.Should().Be("name");
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ALongDescription_IsInvalidAgent()
    {
        var created = await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(), new("bot", new string('d', 501)));

        created.Status.Should().Be(AgentStatus.InvalidAgent);
        created.Detail.Should().Be("Agent description must not exceed 500 characters.");
        created.Field.Should().Be("description");
    }

    [Fact]
    public async Task Create_ATakenName_IsNameTaken()
    {
        var created = await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(), new(" bot "));

        created.Status.Should().Be(AgentStatus.NameTaken);
        created.Detail.Should().Be("An agent named 'bot' already exists.");
        created.Field.Should().Be("name");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_StoresTrimmedValues_AndAuditsTheName()
    {
        _agents.CreateAsync(Arg.Any<CreateAgentRequest>(), _callerId, Arg.Any<CancellationToken>()).Returns(Agent("bot", "does things"));

        var created = await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(), new(" bot ", " does things "));

        created.Status.Should().Be(AgentStatus.Success);
        await _agents.Received(1).CreateAsync(new CreateAgentRequest("bot", "does things"), _callerId, Arg.Any<CancellationToken>());
        AuditCall()[0].Should().Be("agent.created");
        AuditCall()[1].Should().Be("agent");
        AuditCall()[2].Should().Be(_agentId.ToString());
        AuditCall()[3].Should().BeEquivalentTo(new { Name = "bot" });
    }

    [Fact]
    public async Task Create_ABlankDescription_IsNone()
    {
        _agents.CreateAsync(Arg.Any<CreateAgentRequest>(), _callerId, Arg.Any<CancellationToken>()).Returns(Agent());

        await new CreateAgent(_agents, _authorizer, _audit).RunAsync(Caller(), new("bot", "   "));

        await _agents.Received(1).CreateAsync(new CreateAgentRequest("bot", null), _callerId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, "agent.enabled")]
    [InlineData(false, "agent.disabled")]
    public async Task SetActive_AuditsWhichWay(bool active, string audited)
    {
        _agents.SetActiveAsync(_agentId, active, Arg.Any<CancellationToken>()).Returns(true);

        var set = await new SetAgentActive(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, active));

        set.Result.Should().Be(_agentId);
        AuditCall()[0].Should().Be(audited);
        AuditCall()[2].Should().Be(_agentId.ToString());
    }

    [Fact]
    public async Task SetActive_AMissingAgent_IsNotFound()
    {
        var set = await new SetAgentActive(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, true));

        set.Status.Should().Be(AgentStatus.NotFound);
        set.Detail.Should().Be("Agent not found");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_AuditsTheDeletion_AndAMissingAgentIsNotFound()
    {
        _agents.DeleteAsync(_agentId, Arg.Any<CancellationToken>()).Returns(true);

        var deleted = await new DeleteAgent(_agents, _authorizer, _audit).RunAsync(Caller(), _agentId);
        var missing = await new DeleteAgent(_agents, _authorizer, _audit).RunAsync(Caller(), Guid.NewGuid());

        deleted.Result.Should().Be(_agentId);
        AuditCall()[0].Should().Be("agent.deleted");
        missing.Status.Should().Be(AgentStatus.NotFound);
        missing.Detail.Should().Be("Agent not found");
    }

    // ── keys ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListKeys_AMissingAgent_IsNotFound_NotAnEmptyList()
    {
        var listed = await new ListAgentKeys(_agents, _authorizer).RunAsync(Caller(), _agentId);

        listed.Status.Should().Be(AgentStatus.NotFound);
        listed.Detail.Should().Be("Agent not found");
    }

    [Theory]
    [InlineData("k")]
    [InlineData("  ")]
    public async Task CreateKey_ABadName_IsInvalidKey(string name)
    {
        var created = await new CreateAgentKey(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, new(name)));

        created.Status.Should().Be(AgentStatus.InvalidKey);
        created.Detail.Should().Be("Agent key name must be 2-64 characters.");
        created.Field.Should().Be("name");
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateKey_AScopeAnAgentKeyCantHold_IsInvalidKey()
    {
        var created = await new CreateAgentKey(_agents, _authorizer, _audit)
            .RunAsync(Caller(), new(_agentId, new("key", ["knowledge:read", "admin:users"])));

        created.Status.Should().Be(AgentStatus.InvalidKey);
        created.Detail.Should().Be("'admin:users' is an invalid agent key scope. Scopes: knowledge:read, knowledge:write.");
        created.Field.Should().Be("scopes");
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateKey_TheRetiredAgentIngestScope_IsInvalidKey()
    {
        // No check ever required agent:ingest; knowledge:write is what uploads and deletes (CON-171).
        var created = await new CreateAgentKey(_agents, _authorizer, _audit)
            .RunAsync(Caller(), new(_agentId, new("key", ["knowledge:read", "agent:ingest"])));

        created.Status.Should().Be(AgentStatus.InvalidKey);
        created.Detail.Should().Be("'agent:ingest' is an invalid agent key scope. Scopes: knowledge:read, knowledge:write.");
        created.Field.Should().Be("scopes");
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateKey_ANullScopeBeforeAForbiddenOne_IsInvalidKey()
    {
        var created = await new CreateAgentKey(_agents, _authorizer, _audit)
            .RunAsync(Caller(), new(_agentId, new("key", [null!, "admin:users"])));

        created.Status.Should().Be(AgentStatus.InvalidKey);
        created.Detail.Should().Be("'' is an invalid agent key scope. Scopes: knowledge:read, knowledge:write.");
        _agents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateKey_AMissingAgent_IsNotFound()
    {
        var created = await new CreateAgentKey(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, new("key")));

        created.Status.Should().Be(AgentStatus.NotFound);
        created.Detail.Should().Be("Agent not found");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task CreateKey_AuditsTheNameAndId_NeverTheToken()
    {
        _agents.CreateKeyAsync(_agentId, Arg.Any<CreateAgentKeyRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CreateAgentKeyResponse(_keyId, _agentId.ToString(), "the-token", ["knowledge:read"], Epoch, null));

        var created = await new CreateAgentKey(_agents, _authorizer, _audit)
            .RunAsync(Caller(), new(_agentId, new(" key ", ["knowledge:read", "knowledge:read"], RequiresApproval: false)));

        created.Result!.Token.Should().Be("the-token");
        await _agents.Received(1).CreateKeyAsync(_agentId,
            Arg.Is<CreateAgentKeyRequest>(r => r.Name == "key" && r.Scopes!.SequenceEqual(new[] { "knowledge:read" }) && !r.RequiresApproval),
            Arg.Any<CancellationToken>());
        AuditCall()[0].Should().Be("agent.key.created");
        AuditCall()[2].Should().Be(_agentId.ToString());
        AuditCall()[3].Should().BeEquivalentTo(new { Name = "key", KeyId = _keyId });
    }

    [Fact]
    public async Task RevokeKey_AuditsTheKey_AndAMissingKeyIsKeyNotFound()
    {
        _agents.RevokeKeyAsync(_agentId, _keyId, Arg.Any<CancellationToken>()).Returns(true);

        var revoked = await new RevokeAgentKey(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, _keyId));
        var missing = await new RevokeAgentKey(_agents, _authorizer, _audit).RunAsync(Caller(), new(_agentId, Guid.NewGuid()));

        revoked.Result.Should().Be(_keyId);
        AuditCall()[0].Should().Be("agent.key.revoked");
        AuditCall()[3].Should().BeEquivalentTo(new { KeyId = _keyId });
        missing.Status.Should().Be(AgentStatus.KeyNotFound);
        missing.Detail.Should().Be("Key not found");
    }
}
