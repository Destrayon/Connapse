using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Approvals;
using Connapse.Core;
using Connapse.Identity.Authentication;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Connapse.Integration.Tests.Authorization;

/// <summary>
/// The approval round trip for #782: a destructive MCP call waits, a person approves it in the UI, the
/// same call then runs exactly once, and a token created with approval off runs without waiting.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class McpApprovalTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ContainerDelete_ApprovedInUi_RunsOnceThenWaitsAgain()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync(requiresApproval: true);
        string target = await CreateContainerAsync(http, "approval-once");
        string other = await CreateContainerAsync(http, "approval-other");
        try
        {
            var call = new Dictionary<string, object?> { ["containerId"] = target };
            Text(await agent.CallToolAsync("container_delete", call)).Should().Contain("pending approval");
            var pending = await PendingForAsync("container_delete", target);

            // A request isn't approved from another surface, or by a bearer token that reached the page.
            (await DecideAsync(pending.Id, OperationSurface.Rest)).Status.Should().Be(McpApprovalOutcome.NotFromUi);
            (await DecideAsync(pending.Id, OperationSurface.Ui, "AuthenticationTypes.Federation")).Status.Should().Be(McpApprovalOutcome.NotSignedIn);
            (await DecideAsync(pending.Id, OperationSurface.Ui)).Succeeded.Should().BeTrue();

            // The approval is bound to its arguments: another container stays put.
            Text(await agent.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = other }))
                .Should().Contain("pending approval");
            (await http.GetAsync($"/api/containers/{other}")).IsSuccessStatusCode.Should().BeTrue();

            var ran = await agent.CallToolAsync("container_delete", call);
            ran.IsError.Should().NotBe(true, Text(ran));
            Text(ran).Should().NotContain("pending approval");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeFalse("the approved call ran");

            (await DecideAsync(pending.Id, OperationSurface.Ui)).Status.Should().Be(McpApprovalOutcome.NotPending, "an approval is used once");
            Text(await agent.CallToolAsync("container_delete", call)).Should().Contain("pending approval");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{target}");
            await http.DeleteAsync($"/api/containers/{other}");
        }
    }

    [Fact]
    public async Task ContainerDelete_TokenWithApprovalOff_RunsWithoutWaiting()
    {
        var http = fixture.AdminClient;
        await using var agent = await AgentAsync(requiresApproval: false);
        string target = await CreateContainerAsync(http, "approval-off");
        try
        {
            var result = await agent.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = target });

            Text(result).Should().NotContain("pending approval");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeFalse();
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{target}");
        }
    }

    [Fact]
    public async Task ContainerDelete_ApprovedForOneOAuthClient_AnotherClientOfTheSameUserStillWaits()
    {
        var http = fixture.AdminClient;
        await using var asker = await OAuthClientAsync();
        await using var other = await OAuthClientAsync();
        string target = await CreateContainerAsync(http, "approval-client");
        try
        {
            var call = new Dictionary<string, object?> { ["containerId"] = target };
            Text(await asker.CallToolAsync("container_delete", call)).Should().Contain("pending approval");
            (await DecideAsync((await PendingForAsync("container_delete", target)).Id, OperationSurface.Ui)).Succeeded.Should().BeTrue();

            Text(await other.CallToolAsync("container_delete", call)).Should().Contain("pending approval",
                "the approval belongs to the client that asked for it, not to every client of its user");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeTrue();

            var ran = await asker.CallToolAsync("container_delete", call);
            Text(ran).Should().NotContain("pending approval");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeFalse("the asking client's approval still works");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{target}");
        }
    }

    [Fact]
    public async Task ContainerDelete_ApprovedForABrowserSession_TheSameUsersSignInTokenStillWaits()
    {
        var http = fixture.AdminClient;
        await using var session = await BrowserSessionAsync();
        await using var signIn = await McpClientAsync(new AuthenticationHeaderValue("Bearer", fixture.AdminToken));
        string target = await CreateContainerAsync(http, "approval-session");
        try
        {
            var call = new Dictionary<string, object?> { ["containerId"] = target };
            Text(await session.CallToolAsync("container_delete", call)).Should().Contain("pending approval");
            (await DecideAsync((await PendingForAsync("container_delete", target)).Id, OperationSurface.Ui)).Succeeded.Should().BeTrue();

            Text(await signIn.CallToolAsync("container_delete", call)).Should().Contain("pending approval",
                "the approval belongs to the browser session that asked for it, not to the same user's sign-in token");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeTrue();

            var ran = await session.CallToolAsync("container_delete", call);
            Text(ran).Should().NotContain("pending approval");
            (await http.GetAsync($"/api/containers/{target}")).IsSuccessStatusCode.Should().BeFalse("the session's approval still works");
        }
        finally
        {
            await http.DeleteAsync($"/api/containers/{target}");
        }
    }

    /// <summary>An administrator signed in to the UI in a browser, who may decide anyone's request.</summary>
    private static OperationContext Admin(OperationSurface surface, string authenticationType = "Identity.Application") => new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Admin")], authenticationType)),
        surface);

    private async Task<McpApprovalItem> PendingForAsync(string tool, string containerId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var listed = await scope.ServiceProvider.GetRequiredService<ListMcpApprovals>().ExecuteAsync(Admin(OperationSurface.Ui));
        return listed.Result!.Should().ContainSingle(a => a.ToolName == tool && a.ArgumentsSummary.Contains(containerId)).Subject;
    }

    private async Task<Attempt<bool, McpApprovalOutcome>> DecideAsync(Guid id, OperationSurface surface, string authenticationType = "Identity.Application")
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DecideMcpApproval>().ExecuteAsync(Admin(surface, authenticationType), id, approve: true);
    }

    [Fact]
    public async Task Decide_ApproveAndDenyRacing_OnlyOneLands()
    {
        await using var agent = await AgentAsync(requiresApproval: true);
        string target = await CreateContainerAsync(fixture.AdminClient, "approval-race");
        try
        {
            await agent.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = target });
            var pending = await PendingForAsync("container_delete", target);

            var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<DecideMcpApproval>()
                    .ExecuteAsync(Admin(OperationSurface.Ui), pending.Id, approve: i % 2 == 0);
            }));

            decisions.Count(d => d.Succeeded).Should().Be(1, "a request is decided once");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{target}");
        }
    }

    private async Task<McpClient> AgentAsync(bool requiresApproval)
    {
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/v1/agents/", new CreateAgentRequest($"approval-{Guid.NewGuid():N}"));
        var agent = (await created.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<AgentDto>())!;
        var issued = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/agents/{agent.Id}/keys",
            new CreateAgentKeyRequest("approval", ["knowledge:read", "knowledge:write"], RequiresApproval: requiresApproval));
        var key = (await issued.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<CreateAgentKeyResponse>())!;

        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyAuthenticationOptions.HeaderName, key.Token);
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));
    }

    /// <summary>An MCP client holding an OAuth token that a newly registered client was issued for the administrator.</summary>
    private async Task<McpClient> OAuthClientAsync()
    {
        const string redirect = "http://127.0.0.1:8765/callback";
        using var anonymous = fixture.Factory.CreateClient();
        using var registered = await anonymous.PostAsJsonAsync("/oauth/register",
            new { client_name = $"approval-{Guid.NewGuid():N}", redirect_uris = new[] { redirect }, application_type = "native" });
        registered.EnsureSuccessStatusCode();
        string clientId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string code;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            code = await scope.ServiceProvider.GetRequiredService<OAuthAuthCodeService>().CreateAsync(
                AdminUserId(), clientId, Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))), redirect, "knowledge:read knowledge:write");
        using var exchanged = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
        }));
        exchanged.EnsureSuccessStatusCode();

        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await exchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString());
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));
    }

    /// <summary>
    /// An MCP client holding the administrator's browser sign-in cookie, issued the way SignInManager
    /// issues it, so the request authenticates through the Identity.Application cookie scheme.
    /// </summary>
    private async Task<McpClient> BrowserSessionAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<ConnapseUser>>()
            .CreateUserPrincipalAsync((await users.FindByEmailAsync(SharedWebAppFixture.AdminEmail))!);
        var cookie = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        string ticket = cookie.TicketDataFormat.Protect(new AuthenticationTicket(principal,
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) },
            IdentityConstants.ApplicationScheme));

        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Add("Cookie", $"{cookie.Cookie.Name}={ticket}");
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));
    }

    private async Task<McpClient> McpClientAsync(AuthenticationHeaderValue authorization)
    {
        var http = fixture.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = authorization;
        return await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<string> CreateContainerAsync(HttpClient http, string prefix)
    {
        var response = await http.PostAsJsonAsync("/api/containers", new { name = $"{prefix}-{Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }
}
