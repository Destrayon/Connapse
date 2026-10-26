using Connapse.Application;
using Connapse.Application.Agents;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // Agents are identities, so managing them is user management: the scope too, so an administrator's
        // token issued for less (knowledge:read) can't mint an agent a key.
        var group = app.MapGroup("/api/v1/agents")
            .WithTags("Agents")
            .RequireAuthorization("RequireAdmin", "Scope:AdminUsers")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // POST /api/v1/agents — create a new agent
        group.MapPost("/", async (
            [FromBody] CreateAgentRequest request,
            [FromServices] CreateAgent create,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), request, ct);
            return created.Succeeded
                ? Results.Created($"/api/v1/agents/{created.Result!.Id}", created.Result)
                : Problem(created);
        })
        .Produces<AgentDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("agents.create")
        .WithDescription("Create a new agent (Admin only). The name is 2-64 letters, digits, hyphens and underscores, unique among agents.");

        // GET /api/v1/agents — list all agents
        group.MapGet("/", async (
            [FromServices] ListAgents list,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await list.RunAsync(Rest(http), ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<IReadOnlyList<AgentDto>>()
        .WithName("agents.list")
        .WithDescription("List all agents (Admin only)");

        // GET /api/v1/agents/{id} — get agent details including key list
        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] GetAgent get,
            HttpContext http,
            CancellationToken ct) =>
        {
            var got = await get.RunAsync(Rest(http), id, ct);
            return got.Succeeded ? Results.Ok(got.Result) : OperationProblems.Result(got);
        })
        .Produces<AgentDto>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.get")
        .WithDescription("Get agent details (Admin only)");

        // DELETE /api/v1/agents/{id} — soft-delete agent and revoke all its keys
        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] DeleteAgent delete,
            HttpContext http,
            CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), id, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.delete")
        .WithDescription("Delete an agent and revoke all its API keys (Admin only)");

        // PUT /api/v1/agents/{id}/active — enable or disable an agent
        group.MapPut("/{id:guid}/active", async (
            Guid id,
            [FromBody] SetAgentActiveRequest request,
            [FromServices] SetAgentActive setActive,
            HttpContext http,
            CancellationToken ct) =>
        {
            var set = await setActive.RunAsync(Rest(http), new SetAgentActiveCommand(id, request.IsActive), ct);
            return set.Succeeded ? Results.NoContent() : OperationProblems.Result(set);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.setActive")
        .WithDescription("Enable or disable an agent (Admin only)");

        // GET /api/v1/agents/{id}/keys — list API keys for an agent
        group.MapGet("/{id:guid}/keys", async (
            Guid id,
            [FromServices] ListAgentKeys listKeys,
            HttpContext http,
            CancellationToken ct) =>
        {
            var listed = await listKeys.RunAsync(Rest(http), id, ct);
            return listed.Succeeded ? Results.Ok(listed.Result) : OperationProblems.Result(listed);
        })
        .Produces<IReadOnlyList<AgentKeyListItem>>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.keys.list")
        .WithDescription("List API keys for an agent, revoked ones included (Admin only)");

        // POST /api/v1/agents/{id}/keys — create a new API key for an agent
        group.MapPost("/{id:guid}/keys", async (
            Guid id,
            [FromBody] CreateAgentKeyRequest request,
            [FromServices] CreateAgentKey createKey,
            HttpContext http,
            CancellationToken ct) =>
        {
            var created = await createKey.RunAsync(Rest(http), new CreateAgentKeyCommand(id, request), ct);
            return created.Succeeded
                ? Results.Created($"/api/v1/agents/{id}/keys/{created.Result!.KeyId}", created.Result)
                : Problem(created);
        })
        .Produces<CreateAgentKeyResponse>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.keys.create")
        .WithDescription("Create an API key for an agent, with scopes from knowledge:read, knowledge:write and agent:ingest. Token is shown only once. (Admin only)");

        // DELETE /api/v1/agents/{id}/keys/{keyId} — revoke an API key
        group.MapDelete("/{id:guid}/keys/{keyId:guid}", async (
            Guid id,
            Guid keyId,
            [FromServices] RevokeAgentKey revokeKey,
            HttpContext http,
            CancellationToken ct) =>
        {
            var revoked = await revokeKey.RunAsync(Rest(http), new AgentKeyTarget(id, keyId), ct);
            return revoked.Succeeded ? Results.NoContent() : OperationProblems.Result(revoked);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("agents.keys.revoke")
        .WithDescription("Revoke an agent API key (Admin only)");

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);

    /// <summary>
    /// A failed create's problem. Before agents moved onto their operations (CON-71), a bad name or description
    /// was answered with these codes in <c>error</c>, which scripts branch on, so they keep them.
    /// </summary>
    private static IResult Problem<TResult>(Attempt<TResult, AgentStatus> failed)
    {
        var problem = OperationProblems.For(failed.Status, failed.Detail);
        if (failed.Field is { } field)
            problem.Extensions["field"] = field;
        if (ErrorCode(failed.Status, failed.Field) is { } code)
            problem.Extensions["error"] = code;
        return Results.Problem(problem);
    }

    private static string? ErrorCode(AgentStatus status, string? field) => (status, field) switch
    {
        (AgentStatus.InvalidAgent, "name") => "agent_name_invalid",
        (AgentStatus.InvalidAgent, "description") => "agent_description_too_long",
        (AgentStatus.InvalidKey, "name") => "agent_key_name_invalid",
        _ => null,
    };
}
