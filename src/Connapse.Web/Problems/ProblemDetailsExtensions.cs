using Microsoft.AspNetCore.Diagnostics;

namespace Connapse.Web.Problems;

/// <summary>
/// One error format for the REST API: every failure under <c>/api</c> leaves as
/// <c>application/problem+json</c> with an <c>error</c> member. The OAuth protocol endpoints and
/// <c>/mcp</c> are outside <c>/api</c> and keep the error shapes their standards define.
/// </summary>
public static class ProblemDetailsExtensions
{
    public static IServiceCollection AddConnapseProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            // A validation problem's own text is generic, so scripts reading .error get the first
            // rule's message, which is the text the handler's own check used to return.
            string? first = (problem as HttpValidationProblemDetails)?.Errors.Values.SelectMany(m => m).FirstOrDefault();
            problem.Extensions.TryAdd("error", first ?? problem.Detail ?? problem.Title);
        });

    public static bool IsApiRequest(this HttpContext context) =>
        context.Request.Path.StartsWithSegments("/api");

    /// <summary>
    /// For API requests, turns unhandled exceptions into a 500 problem with no exception details
    /// (a bad request body into its 400), and gives empty error responses, such as authentication
    /// challenges, unknown routes and malformed bodies, a problem body.
    /// </summary>
    public static IApplicationBuilder UseApiProblemDetails(this IApplicationBuilder app) =>
        app.UseWhen(IsApiRequest, api => api
            .UseExceptionHandler(new ExceptionHandlerOptions
            {
                StatusCodeSelector = ex => ex switch
                {
                    BadHttpRequestException bad => bad.StatusCode,
                    // A body naming no kind for a polymorphic member, such as a source scope with no "type":
                    // System.Text.Json reports it as unsupported rather than malformed (#859).
                    NotSupportedException { Source: "System.Text.Json" } unsupported
                        when unsupported.Message.StartsWith("The JSON payload for polymorphic", StringComparison.Ordinal) => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                },
            })
            .UseStatusCodePages());

    /// <summary>Rewrites every 4xx or 5xx result the group's handlers return into ProblemDetails.</summary>
    public static TBuilder WithProblemResults<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, ProblemResultFilter>();
}
