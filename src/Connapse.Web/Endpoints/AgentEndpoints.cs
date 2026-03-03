using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Web.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agents")
            .WithTags("Agents")
            .RequireAuthorization("RequireAdmin");

        // POST /api/v1/agents — create a new agent
        group.MapPost("/", async (
            [FromBody] CreateAgentRequest request,
            HttpContext httpContext,
            [FromServices] IAgentService agentService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var adminUserId = GetUserId(httpContext);
            if (adminUserId is null)
                return Results.Unauthorized();

            try
            {
                var agent = await agentService.CreateAsync(request, adminUserId.Value, ct);
                await auditLogger.LogAsync("agent.created", "agent", agent.Id.ToString(),
                    new { agent.Name }, ct);
                return Results.Created($"/api/v1/agents/{agent.Id}", agent);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "ix_agents_name"))
            {
                return Results.Conflict(new { error = $"An agent named '{request.Name}' already exists." });
            }
        })
        .WithName("CreateAgent")
        .WithDescription("Create a new agent (Admin only)");

        // GET /api/v1/agents — list all agents
        group.MapGet("/", async (
            [FromServices] IAgentService agentService,
            CancellationToken ct) =>
        {
            var agents = await agentService.ListAsync(ct);
            return Results.Ok(agents);
        })
        .WithName("ListAgents")
        .WithDescription("List all agents (Admin only)");

        // GET /api/v1/agents/{id} — get agent details including key list
        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] IAgentService agentService,
            CancellationToken ct) =>
        {
            var agent = await agentService.GetAsync(id, ct);
            return agent is null ? Results.NotFound() : Results.Ok(agent);
        })
        .WithName("GetAgent")
        .WithDescription("Get agent details (Admin only)");

        // DELETE /api/v1/agents/{id} — soft-delete agent and revoke all its keys
        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] IAgentService agentService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var deleted = await agentService.DeleteAsync(id, ct);
            if (!deleted)
                return Results.NotFound();

            await auditLogger.LogAsync("agent.deleted", "agent", id.ToString(), null, ct);
            return Results.NoContent();
        })
        .WithName("DeleteAgent")
        .WithDescription("Delete an agent and revoke all its API keys (Admin only)");

        // PUT /api/v1/agents/{id}/active — enable or disable an agent
        group.MapPut("/{id:guid}/active", async (
            Guid id,
            [FromBody] SetAgentActiveRequest request,
            [FromServices] IAgentService agentService,
            CancellationToken ct) =>
        {
            var updated = await agentService.SetActiveAsync(id, request.IsActive, ct);
            return updated ? Results.NoContent() : Results.NotFound();
        })
        .WithName("SetAgentActive")
        .WithDescription("Enable or disable an agent (Admin only)");

        // GET /api/v1/agents/{id}/keys — list API keys for an agent
        group.MapGet("/{id:guid}/keys", async (
            Guid id,
            [FromServices] IAgentService agentService,
            CancellationToken ct) =>
        {
            var keys = await agentService.ListKeysAsync(id, ct);
            return Results.Ok(keys);
        })
        .WithName("ListAgentKeys")
        .WithDescription("List API keys for an agent (Admin only)");

        // POST /api/v1/agents/{id}/keys — create a new API key for an agent
        group.MapPost("/{id:guid}/keys", async (
            Guid id,
            [FromBody] CreateAgentKeyRequest request,
            [FromServices] IAgentService agentService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            try
            {
                var key = await agentService.CreateKeyAsync(id, request, ct);
                await auditLogger.LogAsync("agent.key.created", "agent", id.ToString(),
                    new { request.Name, key.KeyId }, ct);
                return Results.Created($"/api/v1/agents/{id}/keys/{key.KeyId}", key);
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
        .WithName("CreateAgentKey")
        .WithDescription("Create an API key for an agent. Token is shown only once. (Admin only)");

        // DELETE /api/v1/agents/{id}/keys/{keyId} — revoke an API key
        group.MapDelete("/{id:guid}/keys/{keyId:guid}", async (
            Guid id,
            Guid keyId,
            [FromServices] IAgentService agentService,
            [FromServices] IAuditLogger auditLogger,
            CancellationToken ct) =>
        {
            var revoked = await agentService.RevokeKeyAsync(id, keyId, ct);
            if (!revoked)
                return Results.NotFound();

            await auditLogger.LogAsync("agent.key.revoked", "agent", id.ToString(),
                new { KeyId = keyId }, ct);
            return Results.NoContent();
        })
        .WithName("RevokeAgentKey")
        .WithDescription("Revoke an agent API key (Admin only)");

        return app;
    }

    private static Guid? GetUserId(HttpContext httpContext)
    {
        // Only return a user ID for human users, not agents
        if (httpContext.User.FindFirstValue("actor_type") == "agent")
            return null;

        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idClaim, out var id) ? id : null;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex, string indexName) =>
        ex.InnerException?.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase) == true;
}
