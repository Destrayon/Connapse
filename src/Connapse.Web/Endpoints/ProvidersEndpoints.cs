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

        return app;
    }

    private static OperationContext Rest(HttpContext http) => new(http.User, OperationSurface.Rest);
}
