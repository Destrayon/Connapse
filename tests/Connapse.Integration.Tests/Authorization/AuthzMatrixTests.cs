using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// Proof test for #793 (P6, P9): every principal is let in or refused exactly as the route's
/// expectation says. Today's gaps are in known-authz-violations.json under "matrix ".
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public partial class AuthzMatrixTests(SharedWebAppFixture fixture)
{
    /// <summary>
    /// Every route that declares authorization, called as every principal through the real
    /// authentication handlers. The probe answers a request that gets in before the endpoint runs,
    /// so nothing is changed. A route that declares nothing lets everyone in by construction; the
    /// declared test already reports it when its expectation is stricter.
    /// </summary>
    [Fact]
    public async Task AuthzMatrix_RouteByRole_MatchesExpectation()
    {
        var table = AuthzExpectations.Table.Value.Routes;
        var probed = AuthzExpectations.Endpoints(fixture)
            .Where(e => AuthzExpectations.Declared(e.Value) != "anonymous")
            .ToList();

        var violations = new List<string>();
        var undecided = new List<string>();
        foreach (var principal in AuthzPrincipal.All)
        {
            using var http = await AuthzPrincipals.ClientAsync(fixture, principal);
            foreach (var (key, endpoint) in probed)
            {
                bool expected = ExpectedAllowed(principal, table[key]);
                var methods = Methods(endpoint);
                foreach (string method in methods)
                {
                    string row = methods.Count > 1 ? $"{key} via {method}" : key;
                    var (allowed, status) = await ProbeAsync(http, endpoint, method);
                    if (allowed is null)
                        undecided.Add($"{row} as {principal}: {status}");
                    else if (allowed != expected)
                        violations.Add($"matrix {row} as {principal}: expects {(expected ? "allowed" : "refused")}, got {(allowed.Value ? "allowed" : "refused")}");
                }
            }
        }

        undecided.Should().BeEmpty("every probe must end in the probe's answer or an authorization refusal; any other status means authorization never decided");
        AuthzExpectations.ShouldMatchKnown(violations, "matrix ");
    }

    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal)
    {
        ["Viewer"] = 1, ["Editor"] = 2, ["Admin"] = 3, ["Owner"] = 4,
        ["viewer"] = 1, ["editor"] = 2, ["admin"] = 3, ["owner"] = 4,
    };

    /// <summary>The expectation table's rule, applied to one principal.</summary>
    public static bool ExpectedAllowed(AuthzPrincipal principal, AuthzExpectation expectation)
    {
        if (expectation.Requires == "anonymous")
            return true;
        if (principal.Kind == PrincipalKind.Anonymous)
            return false;

        bool tokenAllows = principal.Scopes is null
            || (expectation.Scope is not null && principal.Scopes.Contains(expectation.Scope));
        int rank = principal.Role is null ? 0 : Rank[principal.Role];

        return expectation.Requires switch
        {
            "user" => principal.Kind == PrincipalKind.Person && tokenAllows,
            "mcp" => principal.Kind == PrincipalKind.Agent
                     || (rank >= Rank["admin"] && tokenAllows),
            _ => principal.Kind == PrincipalKind.Person && rank >= Rank[expectation.Requires!] && tokenAllows,
        };
    }

    /// <summary>Every method the route answers; a route that takes any method is probed with GET.</summary>
    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? [.. methods] : ["GET"];

    /// <summary>
    /// Calls the route as the client's principal with the probe header. Allowed only when the probe
    /// answered, refused only on 401, 403, or a redirect to sign-in or access-denied; null for any
    /// other answer, which means something turned the request away before authorization decided.
    /// </summary>
    private static async Task<(bool? Allowed, int Status)> ProbeAsync(HttpClient http, RouteEndpoint endpoint, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), Fill(endpoint.RoutePattern.RawText!));
        request.Headers.Add(AuthzProbe.RequestHeader, "1");
        // Routing turns away a body of the wrong media type with 415 before authorization runs,
        // so the probe sends the type the route accepts.
        var accepts = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()?.ContentTypes;
        if (method is "POST" or "PUT" or "PATCH")
            request.Content = accepts?.Any(t => t.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) == true
                ? new MultipartFormDataContent { { new StringContent("probe"), "probe" } }
                : new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        int status = (int)response.StatusCode;
        if (response.Headers.Contains(AuthzProbe.AllowedHeader) && response.StatusCode == HttpStatusCode.NoContent)
            return (true, status);

        bool refused = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (response.StatusCode == HttpStatusCode.Redirect
                && response.Headers.Location?.OriginalString is { } location
                && (location.Contains("/login", StringComparison.OrdinalIgnoreCase)
                    || location.Contains("/access-denied", StringComparison.OrdinalIgnoreCase)));
        return (refused ? false : null, status);
    }

    /// <summary>A concrete path for a route pattern: a fresh GUID for ids, a placeholder otherwise.</summary>
    public static string Fill(string pattern) => RouteParameter().Replace(pattern, m =>
    {
        string name = m.Groups["name"].Value;
        bool isId = m.Groups["constraint"].Value.Contains("guid", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Id", StringComparison.Ordinal) || name == "id";
        return isId ? Guid.NewGuid().ToString() : "authz-probe";
    });

    [GeneratedRegex(@"\{\*{0,2}(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<constraint>[^}]*)\}")]
    private static partial Regex RouteParameter();
}
