using System.Text.Json;

namespace Connapse.CustomSource.Example;

public static class PermissionEndpoints
{
    public static void MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder access = app.MapGroup("/access/v1").AddEndpointFilter<BearerTokenFilter>();
        access.MapPost("/search/resource", SearchResources);
        access.MapPost("/evaluations", Evaluate);
    }

    private static IResult SearchResources(ResourceSearchRequest request, ExampleConfig config, AccessPolicy policy)
    {
        if (!config.PermissionsEnabled) return Results.NotFound();
        if (!IsUserReading(request.Subject, request.Action) || request.Resource?.Type != "access_key")
            return BadRequest("Expected a user subject, the read action, and resource type access_key.");

        string? scope = request.Resource.Properties?.Scope;
        if (scope is null || !config.HasScope(scope))
            return BadRequest("resource.properties.scope must name a known scope.");

        int offset = 0;
        string token = request.Page?.Token ?? "";
        if (token.Length > 0 && (!int.TryParse(token, out offset) || offset < 0))
            return BadRequest("Malformed page.token.");

        int limit = Math.Clamp(request.Page?.Limit ?? config.PageSize, 1, config.PageSize);
        IReadOnlyList<string> keys = policy.KeysFor(request.Subject!.Id!, scope);
        List<ResourceRef> page = keys.Skip(offset).Take(limit).Select(k => new ResourceRef("access_key", k)).ToList();
        int next = offset + page.Count;

        return Results.Ok(new ResourceSearchResponse(page, new AuthZenPageResponse(next < keys.Count ? next.ToString() : "")));
    }

    private static IResult Evaluate(EvaluationsRequest request, ExampleConfig config, AccessPolicy policy)
    {
        if (!config.PermissionsEnabled) return Results.NotFound();
        if (!IsUserReading(request.Subject, request.Action) || request.Evaluations is null)
            return BadRequest("Expected a user subject, the read action, and an evaluations array.");
        if (request.Evaluations.Count > config.MaxCheckBatch)
            return BadRequest($"Too many evaluations; the limit is {config.MaxCheckBatch}.");

        string userId = request.Subject!.Id!;
        List<DecisionItem> decisions = request.Evaluations
            .Select(item => new DecisionItem(Decide(item, userId, policy)))
            .ToList();

        return Results.Ok(new EvaluationsResponse(decisions));
    }

    /// <summary>False for anything that is not a well-formed document item the user may read.</summary>
    private static bool Decide(JsonElement item, string userId, AccessPolicy policy) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("resource", out JsonElement resource)
        && resource.ValueKind == JsonValueKind.Object
        && Text(resource, "type") == "document"
        && Text(resource, "id") is { Length: > 0 } id
        && resource.TryGetProperty("properties", out JsonElement properties)
        && properties.ValueKind == JsonValueKind.Object
        && Text(properties, "scope") is { Length: > 0 } scope
        && policy.CanRead(userId, scope, id);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsUserReading(AuthZenSubject? subject, AuthZenAction? action) =>
        subject is { Type: "user", Id: { Length: > 0 } } && action?.Name == "read";

    private static IResult BadRequest(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
