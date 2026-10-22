using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Integration.Tests.CrossSurface;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Connapse.Integration.Tests.Authorization;

public enum PrincipalKind { Anonymous, Person, Agent }

/// <summary>
/// One row of the authorization matrix: who is calling and how they signed in. A person signs in
/// with a session token and holds every scope their role implies. A scoped token (a personal access
/// token or an agent key) is limited to <see cref="Scopes"/>, whatever its owner's role.
/// </summary>
public sealed record AuthzPrincipal(string Name, PrincipalKind Kind, string? Role, IReadOnlyList<string>? Scopes)
{
    public static readonly AuthzPrincipal Anonymous = new("anonymous", PrincipalKind.Anonymous, null, null);
    public static readonly AuthzPrincipal NoRole = new("no-role", PrincipalKind.Person, null, null);
    public static readonly AuthzPrincipal Viewer = new("viewer", PrincipalKind.Person, "Viewer", null);
    public static readonly AuthzPrincipal Editor = new("editor", PrincipalKind.Person, "Editor", null);
    public static readonly AuthzPrincipal Admin = new("admin", PrincipalKind.Person, "Admin", null);
    public static readonly AuthzPrincipal Owner = new("owner", PrincipalKind.Person, "Owner", null);
    public static readonly AuthzPrincipal OwnerReadToken = new("owner-read-token", PrincipalKind.Person, "Owner", ["knowledge:read"]);
    public static readonly AuthzPrincipal AgentRead = new("agent-read", PrincipalKind.Agent, null, ["knowledge:read"]);
    public static readonly AuthzPrincipal AgentWrite = new("agent-write", PrincipalKind.Agent, null, ["knowledge:read", "knowledge:write"]);

    public static readonly IReadOnlyList<AuthzPrincipal> All =
        [Anonymous, NoRole, Viewer, Editor, Admin, Owner, OwnerReadToken, AgentRead, AgentWrite];

    public override string ToString() => Name;
}

/// <summary>Signs each matrix principal in once against the shared host.</summary>
public static class AuthzPrincipals
{
    private static readonly Principal EditorUser = new("editor", "editor@authz.connapse.io", "AuthzEditor1!", "Editor");
    private static readonly Principal AdminUser = new("admin-only", "admin@authz.connapse.io", "AuthzAdmin1!", "Admin");

    private static readonly ConcurrentDictionary<string, Lazy<Task<Action<HttpRequestHeaders>>>> Credentials = new(StringComparer.Ordinal);

    /// <summary>A client that doesn't follow redirects, so a page's redirect to /login reads as a refusal.</summary>
    public static async Task<HttpClient> ClientAsync(SharedWebAppFixture fixture, AuthzPrincipal principal)
    {
        var http = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (principal.Kind != PrincipalKind.Anonymous)
            (await CredentialAsync(fixture, principal))(http.DefaultRequestHeaders);
        return http;
    }

    /// <summary>Sets the principal's credential on a request's headers.</summary>
    public static Task<Action<HttpRequestHeaders>> CredentialAsync(SharedWebAppFixture fixture, AuthzPrincipal principal) =>
        Credentials.GetOrAdd(principal.Name, _ => new Lazy<Task<Action<HttpRequestHeaders>>>(() => SignInAsync(fixture, principal))).Value;

    private static async Task<Action<HttpRequestHeaders>> SignInAsync(SharedWebAppFixture fixture, AuthzPrincipal principal)
    {
        if (principal == AuthzPrincipal.OwnerReadToken)
        {
            var pat = await PostAsync<PatCreateResponse>(fixture.AdminClient, "/api/v1/auth/pats",
                new PatCreateRequest($"authz-{Guid.NewGuid():N}", [.. principal.Scopes!]));
            return ApiKey(pat.Token);
        }

        if (principal.Kind == PrincipalKind.Agent)
        {
            var agent = await PostAsync<AgentDto>(fixture.AdminClient, "/api/v1/agents/",
                new CreateAgentRequest($"authz-{principal.Name}-{Guid.NewGuid():N}"));
            var key = await PostAsync<CreateAgentKeyResponse>(fixture.AdminClient, $"/api/v1/agents/{agent.Id}/keys",
                new CreateAgentKeyRequest("authz", [.. principal.Scopes!]));
            return ApiKey(key.Token);
        }

        var user = principal.Name switch
        {
            "no-role" => Principal.NoRole,
            "viewer" => Principal.Viewer,
            "editor" => EditorUser,
            "admin" => AdminUser,
            "owner" => Principal.Admin, // the seeded admin holds Owner and Admin
            _ => throw new ArgumentOutOfRangeException(nameof(principal), principal.Name, null),
        };
        string token = await Principals.TokenAsync(fixture, user);
        return headers => headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static Action<HttpRequestHeaders> ApiKey(string token) =>
        headers => headers.Add(ApiKeyAuthenticationOptions.HeaderName, token);

    private static async Task<T> PostAsync<T>(HttpClient http, string path, object body)
    {
        var response = await http.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
