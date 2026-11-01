using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Connapse.Application;
using Connapse.Identity.Authentication;
using Connapse.Identity.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;

namespace Connapse.Web.Services.GenericApi;

/// <summary>
/// Runs a REST route for the generic connapse_api_* MCP tools (CON-20): in process, as the MCP
/// caller, through the route's own authorization and handler, so the tool applies exactly the rules
/// the API does. Before that it refuses any operation the spec doesn't offer MCP and any whose scope
/// the caller's token lacks, because REST checks only some scopes and a token must not use MCP to
/// reach past the ones it was issued.
/// </summary>
public sealed class ApiDispatcher(
    ApiCatalog catalog,
    IAuthorizationPolicyProvider policies,
    IAuthorizationService authorization,
    IHttpContextAccessor http,
    IServiceProvider services)
{
    private const int MaxResponseChars = 30_000;
    private static readonly string[] ShownHeaders = ["ETag", "Location"];

    public ClaimsPrincipal Caller => http.HttpContext?.User ?? new ClaimsPrincipal();

    /// <summary>The operations the caller's token may use, best match for the query first.</summary>
    public IReadOnlyList<IReadOnlyList<ApiCatalogEntry>> Search(string? query)
    {
        string[] words = (query ?? "").ToLowerInvariant()
            .Split([' ', ',', '.', '/', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries);
        return catalog.ById.Values
            .Where(routes => TokenScopes.Allows(Caller, routes[0].Operation.Scope))
            .Select(routes => (Routes: routes, Score: routes.Max(r => Score(r.Route, words))))
            .Where(m => words.Length == 0 || m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Routes[0].Operation.Id, StringComparer.Ordinal)
            .Select(m => m.Routes)
            .ToList();
    }

    /// <summary>Whether a call to this operation is held for a person to approve before it runs.</summary>
    public bool NeedsApproval(ApiCatalogEntry entry) =>
        entry.Operation.Risk is RiskTier.Destructive or RiskTier.Credential && McpApprovalClaims.Required(Caller);

    public async Task<string> CallAsync(string operationId, bool read, JsonElement? parameters, JsonElement? body, CancellationToken ct)
    {
        var routes = catalog.RoutesFor(operationId);
        if (routes.Count == 0)
            return $"Error: '{operationId}' isn't an operation MCP may call. Find one with connapse_api_search.";
        var operation = routes[0].Operation;
        if (routes[0].Route.IsRead != read)
            return $"Error: '{operationId}' {(read ? "changes data; call it with connapse_api_write" : "only reads; call it with connapse_api_read")}.";
        if (!TokenScopes.Allows(Caller, operation.Scope))
            return $"Error: '{operationId}' needs the {operation.Scope} scope, which this token wasn't issued.";

        if (parameters is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            return "Error: parameters must be a JSON object of parameter names to values.";
        var given = parameters is { ValueKind: JsonValueKind.Object } p
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (Choose(routes, given) is not { } entry)
            return $"Error: '{operationId}' has routes taking different path parameters; give exactly one of these sets: "
                + string.Join("; ", routes.Select(r => string.Join(", ", PathParameters(r.Route).Select(x => x.Name))));
        var route = entry.Route;

        string[] unknown = given.Keys.Where(k => route.Parameters.All(x => x.Name != k)).ToArray();
        if (unknown.Length > 0)
            return $"Error: '{operationId}' has no parameter {string.Join(", ", unknown)}. It takes: "
                + (route.Parameters.Count == 0 ? "none" : string.Join(", ", route.Parameters.Select(x => x.Name))) + ".";
        string[] missing = route.Parameters.Where(x => x.Required && !given.ContainsKey(x.Name)).Select(x => x.Name).ToArray();
        if (missing.Length > 0)
            return $"Error: '{operationId}' needs {string.Join(", ", missing)}.";
        bool hasBody = body is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
        if (hasBody && route.BodyContentType is null)
            return $"Error: '{operationId}' takes no body.";
        if (route.BodyContentType is not (null or "application/json"))
            return $"Error: '{operationId}' takes a {route.BodyContentType} body, which this tool can't send.";
        if (!hasBody && route.BodyRequired)
            return $"Error: '{operationId}' needs a body. Its schema is in connapse_api_search.";

        var context = Request(entry, given, hasBody ? body!.Value.GetRawText() : null, ct);
        if (await AuthorizeAsync(entry.Endpoint, context) is { } refused)
            return refused;
        try
        {
            await entry.Endpoint.RequestDelegate!(context);
        }
        catch (BadHttpRequestException e)
        {
            return $"{e.StatusCode} {ReasonPhrases.GetReasonPhrase(e.StatusCode)}\n{e.Message}";
        }
        return await ResponseAsync(context.Response);
    }

    /// <summary>The route whose path parameters are the ones given; with one route, that route.</summary>
    private static ApiCatalogEntry? Choose(IReadOnlyList<ApiCatalogEntry> routes, Dictionary<string, JsonElement> given)
    {
        if (routes.Count == 1)
            return routes[0];
        var fits = routes.Where(r => PathParameters(r.Route).All(x => given.ContainsKey(x.Name))).ToList();
        int most = fits.Count == 0 ? 0 : fits.Max(r => PathParameters(r.Route).Count());
        var best = fits.Where(r => PathParameters(r.Route).Count() == most).ToList();
        return best.Count == 1 ? best[0] : null;
    }

    private static IEnumerable<ApiParameter> PathParameters(ApiRoute route) => route.Parameters.Where(x => x.In == "path");

    private DefaultHttpContext Request(ApiCatalogEntry entry, Dictionary<string, JsonElement> given, string? body, CancellationToken ct)
    {
        var original = http.HttpContext;
        var context = new DefaultHttpContext { RequestServices = services, User = Caller, RequestAborted = ct };
        if (original is not null)
        {
            context.TraceIdentifier = original.TraceIdentifier;
            context.Connection.RemoteIpAddress = original.Connection.RemoteIpAddress;
            context.Connection.RemotePort = original.Connection.RemotePort;
            context.Request.Scheme = original.Request.Scheme;
            context.Request.Host = original.Request.Host;
            context.Request.PathBase = original.Request.PathBase;
            context.Request.Headers.UserAgent = original.Request.Headers.UserAgent;
        }

        var request = context.Request;
        request.Method = entry.Route.Method;
        string path = entry.Route.Path;
        var query = new QueryBuilder();
        foreach (var parameter in entry.Route.Parameters)
        {
            if (!given.TryGetValue(parameter.Name, out var value) || value.ValueKind is JsonValueKind.Null)
                continue;
            switch (parameter.In)
            {
                case "path":
                    string text = Text(value);
                    request.RouteValues[parameter.Name] = text;
                    path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(text), StringComparison.Ordinal);
                    break;
                case "query":
                    foreach (var item in value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value])
                        query.Add(parameter.Name, Text(item));
                    break;
                case "header":
                    request.Headers[parameter.Name] = Text(value);
                    break;
            }
        }
        request.Path = PathString.FromUriComponent(path);
        request.QueryString = query.ToQueryString();

        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            request.ContentType = "application/json";
            request.ContentLength = bytes.Length;
            request.Body = new MemoryStream(bytes);
            // Minimal APIs read a body only from a request that says it can have one.
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        }
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(entry.Endpoint);
        return context;
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>The route's own authorization, as the authorization middleware would evaluate it for this caller.</summary>
    private async Task<string?> AuthorizeAsync(Endpoint endpoint, HttpContext context)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return null;
        var policy = await AuthorizationPolicy.CombineAsync(
            policies,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
            ?? await policies.GetFallbackPolicyAsync();
        if (policy is null || (await authorization.AuthorizeAsync(Caller, context, policy)).Succeeded)
            return null;
        return "403 Forbidden\nThis caller may not use this route.";
    }

    private static async Task<string> ResponseAsync(HttpResponse response)
    {
        int status = response.StatusCode;
        var stream = (MemoryStream)response.Body;
        string head = $"{status} {ReasonPhrases.GetReasonPhrase(status)}";
        // A caller needs these to act on the answer: an ETag to send back as If-Match, a Location to read next.
        foreach (string name in ShownHeaders)
            if (response.Headers.TryGetValue(name, out var value) && value.Count > 0)
                head += $"\n{name}: {value}";
        if (stream.Length == 0)
            return head;
        string? type = response.ContentType;
        if (type is not null && !type.Contains("json", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return $"{head}\n({stream.Length} bytes of {type}, which this tool doesn't show)";
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string text = await reader.ReadToEndAsync();
        return text.Length <= MaxResponseChars
            ? $"{head}\n{text}"
            : $"{head}\n{text[..MaxResponseChars]}\n… (cut at {MaxResponseChars} of {text.Length} characters; narrow the request, for example with a smaller page size)";
    }

    private static int Score(ApiRoute route, string[] words)
    {
        string id = route.OperationId.ToLowerInvariant();
        string path = route.Path.ToLowerInvariant();
        string text = $"{route.Summary} {string.Join(' ', route.Tags)}".ToLowerInvariant();
        return words.Sum(w => (id.Contains(w) ? 3 : 0) + (path.Contains(w) ? 2 : 0) + (text.Contains(w) ? 1 : 0));
    }
}
