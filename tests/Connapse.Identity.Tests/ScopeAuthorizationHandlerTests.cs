using System.Security.Claims;
using Connapse.Identity.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace Connapse.Identity.Tests;

/// <summary>
/// Unit tests for ScopeAuthorizationHandler — pure logic, no DB required.
/// Verifies that the role→scope mapping and explicit scope claims both grant
/// authorization requirements correctly.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeAuthorizationHandlerTests
{
    private static AuthorizationHandlerContext CreateContext(
        ScopeRequirement requirement,
        IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, null);
    }

    private static async Task<bool> IsGrantedAsync(string scope, IEnumerable<Claim> claims)
    {
        var handler = new ScopeAuthorizationHandler();
        var requirement = new ScopeRequirement(scope);
        var context = CreateContext(requirement, claims);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // ── Role-derived scopes ──────────────────────────────────────────────

    [Fact]
    public async Task HandleRequirement_AdminRole_GrantsKnowledgeRead()
    {
        var granted = await IsGrantedAsync("knowledge:read",
            [new Claim(ClaimTypes.Role, "Admin")]);
        granted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_AdminRole_GrantsKnowledgeWrite()
    {
        var granted = await IsGrantedAsync("knowledge:write",
            [new Claim(ClaimTypes.Role, "Admin")]);
        granted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_ViewerRole_GrantsKnowledgeRead()
    {
        var granted = await IsGrantedAsync("knowledge:read",
            [new Claim(ClaimTypes.Role, "Viewer")]);
        granted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_ViewerRole_DeniesKnowledgeWrite()
    {
        var granted = await IsGrantedAsync("knowledge:write",
            [new Claim(ClaimTypes.Role, "Viewer")]);
        granted.Should().BeFalse();
    }

    [Fact]
    public async Task HandleRequirement_AgentRole_GrantsAgentIngest()
    {
        var granted = await IsGrantedAsync("agent:ingest",
            [new Claim(ClaimTypes.Role, "Agent")]);
        granted.Should().BeTrue();
    }

    [Fact]
    public async Task HandleRequirement_AgentRole_DeniesAdminUsers()
    {
        var granted = await IsGrantedAsync("admin:users",
            [new Claim(ClaimTypes.Role, "Agent")]);
        granted.Should().BeFalse();
    }

    // ── Explicit scope claims ────────────────────────────────────────────

    [Fact]
    public async Task HandleRequirement_ExplicitScopeClaim_GrantsScopeRegardlessOfRole()
    {
        var granted = await IsGrantedAsync("knowledge:write",
        [
            new Claim(ClaimTypes.Role, "Viewer"),   // role alone wouldn't grant write
            new Claim("scope", "knowledge:write"),  // but explicit claim does
        ]);
        granted.Should().BeTrue();
    }

    // ── No claims ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRequirement_NoClaims_DeniesRequirement()
    {
        var granted = await IsGrantedAsync("knowledge:read", []);
        granted.Should().BeFalse();
    }
}
