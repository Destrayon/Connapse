using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Problems;

/// <summary>
/// Rewrites any 4xx or 5xx result an API handler returns into ProblemDetails. The handler's own
/// fields (<c>error</c>, <c>message</c>, <c>field</c>, ...) stay as top-level members, so a
/// script reading <c>.error</c> keeps working.
/// </summary>
public sealed class ProblemResultFilter(IOptions<JsonOptions> json) : IEndpointFilter
{
    // Members ProblemDetails already writes; a handler field with one of these names would be
    // written twice.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "title", "status", "detail", "instance",
    };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        object? result = await next(context);
        return result is IResult http ? ToProblem(http, json.Value.SerializerOptions) ?? result : result;
    }

    /// <summary>The ProblemDetails result for a failure, or null when the result needs no change.</summary>
    public static IResult? ToProblem(IResult result, JsonSerializerOptions options)
    {
        while (result is INestedHttpResult nested)
            result = nested.Result;

        if (result is ProblemHttpResult or ValidationProblem)
            return null;
        if (result is not IStatusCodeHttpResult { StatusCode: >= 400 and int status })
            return null;

        var problem = new ProblemDetails { Status = status };
        object? value = (result as IValueHttpResult)?.Value;
        if (value is string text)
        {
            problem.Detail = text;
        }
        else if (value is not null)
        {
            var body = JsonSerializer.SerializeToElement(value, value.GetType(), options);
            if (body.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in body.EnumerateObject())
                {
                    if (!Reserved.Contains(member.Name))
                        problem.Extensions[member.Name] = member.Value.Clone();
                }
                problem.Detail = Text(body, "detail") ?? Text(body, "message") ?? Text(body, "error");
            }
        }

        return Results.Problem(problem);
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
