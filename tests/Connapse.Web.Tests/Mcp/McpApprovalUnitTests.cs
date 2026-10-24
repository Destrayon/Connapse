using System.Security.Claims;
using System.Text.Json;
using Connapse.Identity.Authentication;
using Connapse.Identity.Authorization;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Connapse.Web.Tests.Mcp;

/// <summary>The pure pieces of MCP approval (#782): which token asks, who owns it, and how a call is identified.</summary>
[Trait("Category", "Unit")]
public class McpApprovalUnitTests
{
    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    [Fact]
    public void Required_NoSetting_RequiresApproval()
    {
        McpApprovalClaims.Required(User(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()))).Should().BeTrue();
    }

    [Fact]
    public void Required_TokenWithApprovalOff_DoesNotRequireIt()
    {
        McpApprovalClaims.Required(User(new Claim(McpApprovalClaims.RequiresApproval, "false"))).Should().BeFalse();
    }

    [Fact]
    public void TokenKeyAndOwner_AgentKey_BindToKeyAndCreator()
    {
        Guid creator = Guid.NewGuid();
        var agent = User(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("agent_key_id", "k1"),
            new Claim("on_behalf_of", creator.ToString()));

        McpApprovalClaims.TokenKey(agent).Should().Be("agent_key:k1");
        McpApprovalClaims.OwnerUserId(agent).Should().Be(creator);
    }

    [Fact]
    public void TokenKey_Pat_BindsToPatNotUser()
    {
        McpApprovalClaims.TokenKey(User(new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim("pat_id", "p1"))).Should().Be("pat:p1");
    }

    [Fact]
    public void CanonicalArguments_KeyOrderAndWhitespace_HashTheSame()
    {
        var a = Arguments("""{ "b": [1, {"y": 2, "x": 1}], "a": "z" }""");
        var b = Arguments("""{"a":"z","b":[1,{"x":1,"y":2}]}""");

        McpApprovalGate.CanonicalArguments(a).Should().Be(McpApprovalGate.CanonicalArguments(b));
    }

    [Fact]
    public void CanonicalArguments_DifferentValues_Differ()
    {
        McpApprovalGate.CanonicalArguments(Arguments("""{"containerId":"one"}"""))
            .Should().NotBe(McpApprovalGate.CanonicalArguments(Arguments("""{"containerId":"two"}""")));
    }

    [Fact]
    public async Task AnonymousDiscovery_ListToolsAnonymously_Succeeds()
    {
        var context = DiscoveryContext(Anonymous, new RequestContext<ListToolsRequestParams>(Substitute.For<McpServer>(), new JsonRpcRequest { Method = "tools/list" }, new ListToolsRequestParams()));
        await new AnonymousMcpDiscoveryHandler().HandleAsync(context);
        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task AnonymousDiscovery_SignedInCaller_LeavesScopeToTheScopeHandler()
    {
        var signedIn = User(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        var context = DiscoveryContext(signedIn, new RequestContext<ListToolsRequestParams>(Substitute.For<McpServer>(), new JsonRpcRequest { Method = "tools/list" }, new ListToolsRequestParams()));
        await new AnonymousMcpDiscoveryHandler().HandleAsync(context);
        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task AnonymousDiscovery_NotAnMcpRequest_DoesNotSucceed()
    {
        var context = DiscoveryContext(Anonymous, resource: null);
        await new AnonymousMcpDiscoveryHandler().HandleAsync(context);
        context.HasSucceeded.Should().BeFalse();
    }

    private static AuthorizationHandlerContext DiscoveryContext(ClaimsPrincipal user, object? resource) =>
        new([new ScopeRequirement("knowledge:write")], user, resource);

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
