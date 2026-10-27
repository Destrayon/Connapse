using Connapse.Application;
using Connapse.Application.Providers;
using Connapse.Core;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Mvc;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Provider setup over REST, on the same operations as the providers page. Settings administrators only:
/// a token of theirs needs the admin:settings scope.
/// </summary>
public static class ProvidersEndpoints
{
    public static IEndpointRouteBuilder MapProvidersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/providers")
            .WithTags("Providers")
            .RequireAuthorization("RequireSettingsAdmin")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // GET /api/v1/providers — each built-in provider and what it still needs
        group.MapGet("/", async (
            [FromServices] GetProviderStatus status,
            HttpContext http,
            CancellationToken ct) =>
        {
            var read = await status.RunAsync(Rest(http), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<IReadOnlyList<ProviderSetup>>()
        .WithName("providers.status")
        .WithDescription("List each built-in provider (aws, azure, github, atlassian) with its requirements and their status. "
            + "Checks AWS, Entra and GitHub live, so it can take a few seconds. A provider's overall status is its weakest requirement's.");

        MapCustomProviders(group.MapGroup("/custom"));
        return app;
    }

    private static void MapCustomProviders(RouteGroupBuilder custom)
    {
        // GET /api/v1/providers/custom — each custom provider and the connections that use it
        custom.MapGet("/", async ([FromServices] ListCustomProviders list, HttpContext http, CancellationToken ct) =>
        {
            var read = await list.RunAsync(Rest(http), ct);
            return read.Succeeded ? Results.Ok(read.Result) : OperationProblems.Result(read);
        })
        .Produces<IReadOnlyList<CustomProviderListing>>()
        .WithName("providers.custom.list")
        .WithDescription("List the custom providers, each with the connections that check permissions with it. Tokens and link secrets are never returned.");

        // POST /api/v1/providers/custom — check a source server and save it as a custom provider
        custom.MapPost("/", async ([FromServices] CreateCustomProvider create, [FromBody] CreateCustomProviderRequest request, HttpContext http, CancellationToken ct) =>
        {
            var created = await create.RunAsync(Rest(http), request, ct);
            return created.Succeeded ? Results.Created((string?)null, created.Result) : OperationProblems.Result(created);
        })
        .Produces<CustomProvider>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.create")
        .WithDescription("Add a custom provider. Its source server is checked first (address, token, that a wrong token is refused, "
            + "and a real permission check), and nothing is saved unless every step passes. A blank name is the one the server reports.");

        // PATCH /api/v1/providers/custom/{providerId} — rename it, move it or replace its token
        custom.MapPatch("/{providerId:guid}", async ([FromServices] UpdateCustomProvider update, Guid providerId,
            [FromBody] UpdateCustomProviderRequest request, HttpContext http, CancellationToken ct) =>
        {
            var updated = await update.RunAsync(Rest(http), providerId, request, ct);
            return updated.Succeeded ? Results.Ok(updated.Result) : OperationProblems.Result(updated);
        })
        .Produces<CustomProvider>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.update")
        .WithDescription("Change a custom provider's name, address or token; what is left out stays. The source server is checked again first. "
            + "A new address needs its token: the saved one is only sent to the saved address. The address can't change while connections use it.");

        // POST /api/v1/providers/custom/{providerId}/check — check its source server, saving nothing
        custom.MapPost("/{providerId:guid}/check", async ([FromServices] CheckCustomProvider check, Guid providerId,
            [FromBody] CustomProviderCheckRequest? request, HttpContext http, CancellationToken ct) =>
        {
            var checkedServer = await check.RunAsync(Rest(http), providerId, request ?? new(), ct);
            return checkedServer.Succeeded ? Results.Ok(checkedServer.Result) : OperationProblems.Result(checkedServer);
        })
        .Produces<CustomProviderCheck>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("providers.custom.check")
        .WithDescription("Check a custom provider's source server, with its saved address and token or the ones given, without saving anything. "
            + "A failed check is still a 200: endpointOk, permissionsOk, error and field say what passed and what to change.");

        // PUT /api/v1/providers/custom/{providerId}/link-secret — a new link secret, shown once
        custom.MapPut("/{providerId:guid}/link-secret", async ([FromServices] ReplaceCustomProviderLinkSecret linkSecret, Guid providerId,
            HttpContext http, CancellationToken ct) =>
        {
            var replaced = await linkSecret.RunAsync(Rest(http), providerId, ct);
            return replaced.Succeeded ? Results.Ok(replaced.Result) : OperationProblems.Result(replaced);
        })
        .Produces<CustomProviderLinkSecret>()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .WithName("providers.custom.linkSecret")
        .WithDescription("Generate a new link secret for a custom provider, so its users can link their accounts. This response is the only time "
            + "it is shown, and any earlier secret stops working at once.");

        // DELETE /api/v1/providers/custom/{providerId}
        custom.MapDelete("/{providerId:guid}", async ([FromServices] DeleteCustomProvider delete, Guid providerId, HttpContext http, CancellationToken ct) =>
        {
            var deleted = await delete.RunAsync(Rest(http), providerId, ct);
            return deleted.Succeeded ? Results.NoContent() : OperationProblems.Result(deleted);
        })
        .Produces(StatusCodes.Status204NoContent)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .WithName("providers.custom.delete")
        .WithDescription("Delete a custom provider with its token and link secret. Refused while connections use it, since their sources would lose their permission checks.");
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
