using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Integration.Tests.Authorization;
using Connapse.Web.Endpoints;
using Connapse.Web.Problems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Connapse.Integration.Tests.Problems;

/// <summary>
/// Proof tests for #833 (epic #783 Phase 2.2, P3): every REST failure is one format, and the
/// rules declared on request records run before any handler.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ProblemDetailsTests(SharedWebAppFixture fixture)
{
    // Refusals come from different handlers: no credential, a session token, a scoped PAT, an agent key.
    private static readonly AuthzPrincipal[] Refused =
        [AuthzPrincipal.Anonymous, AuthzPrincipal.Viewer, AuthzPrincipal.OwnerReadToken, AuthzPrincipal.AgentRead];

    private static IReadOnlyList<(string Key, RouteEndpoint Endpoint)> ApiRoutes(SharedWebAppFixture fixture) =>
        AuthzExpectations.Endpoints(fixture)
            .Where(e => e.Value.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal))
            .Select(e => (e.Key, e.Value))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Fails the four ways an API request fails before or inside a handler, on every /api route:
    /// refused by authentication or authorization (probed, so nothing runs), a malformed JSON
    /// body (binding rejects it before the handler), unknown ids on reads and deletes, and a route
    /// that doesn't exist. Every 4xx and 5xx must be a problem with an <c>error</c> member.
    /// </summary>
    [Fact]
    public async Task Every4xx5xx_IsProblemJson()
    {
        var routes = ApiRoutes(fixture);
        var table = AuthzExpectations.Table.Value.Routes;
        var known = AuthzExpectations.KnownViolations.Value;
        var violations = new List<string>();
        int failures = 0;

        // mustFail rows are failures by construction; one that answers below 400 means that row's
        // failure path was never checked, so it is reported rather than skipped.
        async Task CheckAsync(HttpClient http, HttpRequestMessage request, string row, bool mustFail)
        {
            using (request)
            using (var response = await http.SendAsync(request))
            {
                if ((int)response.StatusCode < 400)
                {
                    if (mustFail)
                        violations.Add($"{row}: expected a failure, got {(int)response.StatusCode}");
                    return;
                }
                failures++;
                if (await NotAProblemAsync(response) is { } why)
                    violations.Add($"{row}: {(int)response.StatusCode} {why}");
            }
        }

        foreach (var principal in Refused)
        {
            using var http = await AuthzPrincipals.ClientAsync(fixture, principal);
            foreach (var (key, endpoint) in routes)
            {
                var methods = Methods(endpoint);
                foreach (string method in methods)
                {
                    // Refused unless the table lets this principal in, or the matrix already lists
                    // the route as wrongly letting it in.
                    string matrixRow = $"matrix {(methods.Count > 1 ? $"{key} via {method}" : key)} as {principal}";
                    bool refused = table.TryGetValue(key, out var expectation)
                        && !AuthzMatrixTests.ExpectedAllowed(principal, expectation)
                        && !known.ContainsKey($"{matrixRow}: expects refused, got allowed");
                    var request = new HttpRequestMessage(new HttpMethod(method), AuthzMatrixTests.Fill(endpoint.RoutePattern.RawText!));
                    request.Headers.Add(AuthzProbe.RequestHeader, "1");
                    if (method is "POST" or "PUT" or "PATCH")
                        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    await CheckAsync(http, request, $"{key} via {method} as {principal}", mustFail: refused);
                }
            }
        }

        using var owner = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Owner);
        foreach (var (key, endpoint) in routes)
        {
            string path = AuthzMatrixTests.Fill(endpoint.RoutePattern.RawText!);
            foreach (string method in Methods(endpoint))
            {
                if (method is "POST" or "PUT" or "PATCH" && AcceptsJson(endpoint))
                {
                    var malformed = new HttpRequestMessage(new HttpMethod(method), path)
                    {
                        Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
                    };
                    await CheckAsync(owner, malformed, $"{key} via {method} with a malformed body", mustFail: true);
                }
                else if (method == "GET" || (method == "DELETE" && OnlyIdParameters(endpoint)))
                {
                    await CheckAsync(owner, new HttpRequestMessage(new HttpMethod(method), path), $"{key} via {method} with unknown ids", mustFail: false);
                }
            }
        }

        await CheckAsync(owner, new HttpRequestMessage(HttpMethod.Get, "/api/v1/no-such-route"), "an unknown /api route", mustFail: true);

        failures.Should().BeGreaterThan(routes.Count, "the probes must actually fail requests, or the check proves nothing");
        violations.Should().BeEmpty();
    }

    /// <summary>
    /// Routes outside /api, which <see cref="Every4xx5xx_IsProblemJson"/> doesn't probe, and why
    /// each keeps its own error shape. Razor pages are left out too: they answer people, not scripts.
    /// </summary>
    private static readonly (string Prefix, string Reason)[] Exempt =
    [
        ("/oauth/", "OAuth 2.1 protocol endpoints answer with RFC 6749 { error, error_description } as application/json, the shape OAuth clients, MCP clients among them, parse"),
        ("/.well-known/", "OAuth discovery documents are defined by RFC 8414 and RFC 9728"),
        ("/mcp", "the MCP transport answers with JSON-RPC errors"),
        ("/_blazor", "Blazor Server's circuit transport speaks SignalR, not REST"),
        ("/_framework/", "Blazor's own navigation plumbing for the pages"),
        ("/hubs/", "the ingestion progress hub speaks SignalR, not REST"),
        ("/health", "the readiness check answers 200 with no body and has no failure response"),
        ("{**path:file}", "the static-file fallback serves the pages' assets"),
    ];

    [Fact]
    public void EveryRouteOutsideApi_IsExemptForAStatedReason()
    {
        var unexplained = AuthzExpectations.Endpoints(fixture)
            .Where(e => !e.Value.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal))
            .Where(e => e.Value.Metadata.GetMetadata<ComponentTypeMetadata>() is null)
            .Where(e => !Exempt.Any(x => e.Value.RoutePattern.RawText!.StartsWith(x.Prefix, StringComparison.Ordinal)))
            .Select(e => e.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        unexplained.Should().BeEmpty("a route outside /api skips the problem format only for a reason listed in Exempt");
    }

    /// <summary>
    /// No Connapse route throws on purpose, so a bare host with the same API pipeline stands in.
    /// It runs as Development, where the developer exception page would otherwise answer.
    /// </summary>
    [Fact]
    public async Task UnhandledException_IsProblemWithoutItsDetails()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Services.AddConnapseProblemDetails();
        await using var app = builder.Build();
        app.UseApiProblemDetails();
        app.MapGet("/api/throws", string () => throw new InvalidOperationException("secret-connection-string"));
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/api/throws");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await NotAProblemAsync(response)).Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("secret-connection-string");
    }

    /// <summary>One invalid body for each request record that declares a validation rule.</summary>
    private static readonly Dictionary<Type, (string Path, object Body, string Member)> InvalidBodies = new()
    {
        [typeof(PatCreateRequest)] = ("/api/v1/auth/pats", new { name = new string('x', 257) }, "Name"),
        [typeof(CreateContainerApiRequest)] = ("/api/containers/", new { name = " " }, "Name"),
        [typeof(CreateFolderRequest)] = ($"/api/containers/{Guid.NewGuid()}/folders/", new { path = "" }, "Path"),
    };

    /// <summary>
    /// Every request record an /api handler binds that declares a rule has a case above, and each
    /// case is refused with a 400 validation problem naming the member. The ids in the paths match
    /// nothing, so a rule that didn't run would surface as a 404 or a handler error instead. The
    /// Core records prove Core's own AddValidation runs; without it their rules are silently skipped.
    /// </summary>
    [Fact]
    public async Task Validation_RunsOnRequestRecords()
    {
        var annotated = ApiRoutes(fixture)
            .SelectMany(r => r.Endpoint.Metadata.OfType<MethodInfo>())
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t.Namespace?.StartsWith("Connapse", StringComparison.Ordinal) == true && DeclaresRule(t))
            .Distinct()
            .ToList();

        annotated.Should().BeEquivalentTo(InvalidBodies.Keys,
            "every request record with a rule needs an invalid-body case here, and every case must still bind a record with a rule");

        using var owner = await AuthzPrincipals.ClientAsync(fixture, AuthzPrincipal.Owner);
        foreach (var (type, (path, body, member)) in InvalidBodies)
        {
            using var response = await owner.PostAsJsonAsync(path, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{type.Name} declares a rule on {member}");
            (await NotAProblemAsync(response)).Should().BeNull($"{type.Name} is refused as a problem");
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            problem.RootElement.TryGetProperty("errors", out var errors).Should().BeTrue($"{type.Name} is refused by validation, not by its handler");
            errors.EnumerateObject().Select(e => e.Name).Should().Contain(n => n.Equals(member, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Why a failure response isn't a well-formed problem, or null when it is.</summary>
    private static async Task<string?> NotAProblemAsync(HttpResponseMessage response)
    {
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type != "application/problem+json")
            return $"is {type ?? "an empty body"}, not application/problem+json";

        string text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        var missing = new[] { "type", "title", "status", "error" }.Where(m => !root.TryGetProperty(m, out _)).ToList();
        if (missing.Count > 0)
            return $"has no {string.Join(", ", missing)}";
        if (root.GetProperty("status").GetInt32() != (int)response.StatusCode)
            return $"says status {root.GetProperty("status").GetInt32()}";
        if (text.Contains("   at ", StringComparison.Ordinal))
            return "includes a stack trace";
        return null;
    }

    private static bool DeclaresRule(Type type) =>
        type.GetProperties().Any(p => p.IsDefined(typeof(ValidationAttribute), inherit: true))
        || type.GetConstructors().SelectMany(c => c.GetParameters()).Any(p => p.IsDefined(typeof(ValidationAttribute), inherit: true));

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? [.. methods] : ["GET"];

    private static bool AcceptsJson(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.Any(t => t.Contains("json", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool OnlyIdParameters(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.Parameters.All(p => p.Name.EndsWith("Id", StringComparison.Ordinal) || p.Name == "id"
            || p.ParameterPolicies.Any(pp => pp.Content?.Contains("guid", StringComparison.OrdinalIgnoreCase) == true));
}
