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
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Store_RequestWithASecret_KeepsItOnlySealedAndMatchesOnlyTheRealCall()
    {
        string tokenKey = $"pat:{Guid.NewGuid()}";
        string secret = $"sealed-{Guid.NewGuid():N}";
        var call = McpCallArguments.From(Arguments(new { operation = "connections.create", body = new { Name = "x", Secret = secret } }));
        var other = McpCallArguments.From(Arguments(new { operation = "connections.create", body = new { Name = "x", Secret = "another" } }));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>();

        var request = await store.RequestAsync(tokenKey, AdminUserId(), "connapse_api_write", call.Canonical, call.Redacted);
        (await store.RequestAsync(tokenKey, AdminUserId(), "connapse_api_write", call.Canonical, call.Redacted)).Id
            .Should().Be(request.Id, "the same call waits on the same request");
        (await store.RequestAsync(tokenKey, AdminUserId(), "connapse_api_write", other.Canonical, other.Redacted)).Id
            .Should().NotBe(request.Id, "two calls that differ only in a secret are different calls");

        var stored = await RowAsync(request.Id);
        string.Join("|", stored.ArgumentsSummary, stored.ArgumentsHash, stored.ArgumentsProtected).Should().NotContain(secret);
        stored.ArgumentsHash.Should().NotBe(Sha256(call.Canonical), "the hash carries nothing derived from the secret")
            .And.NotBe(Sha256(call.Redacted), "a matcher from before CON-185 must not take a call sending the placeholder as the secret");
        stored.ArgumentsSummary.Should().Contain(McpCallArguments.Hidden);
        stored.ArgumentsProtected.Should().NotBeNull();

        (await store.DecideAsync(request.Id, AdminUserId(), isAdmin: true, approve: true)).Should().Be(McpApprovalDecision.Decided);
        (await store.TryConsumeAsync(tokenKey, "connapse_api_write", other.Canonical, other.Redacted)).Should().BeFalse("the approval is for the real secret only");
        (await store.TryConsumeAsync(tokenKey, "connapse_api_write", call.Canonical, call.Redacted)).Should().BeTrue();
        var used = await RowAsync(request.Id);
        used.ArgumentsProtected.Should().BeNull("a used request no longer needs the real arguments");
        used.ArgumentsHash.Should().BeEmpty();
        (await store.TryConsumeAsync(tokenKey, "connapse_api_write", call.Canonical, call.Redacted)).Should().BeFalse("an approval is used once");
    }

    [Fact]
    public async Task Store_DeniedRequestWithASecret_DropsTheSealedArguments()
    {
        string secret = $"denied-{Guid.NewGuid():N}";
        var call = McpCallArguments.From(Arguments(new { body = new { Token = secret } }));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>();

        var request = await store.RequestAsync($"pat:{Guid.NewGuid()}", AdminUserId(), "connapse_api_write", call.Canonical, call.Redacted);
        (await store.DecideAsync(request.Id, AdminUserId(), isAdmin: true, approve: false)).Should().Be(McpApprovalDecision.Decided);

        var denied = await RowAsync(request.Id);
        denied.ArgumentsProtected.Should().BeNull();
        denied.ArgumentsHash.Should().BeEmpty();
        denied.ArgumentsSummary.Should().NotContain(secret);
    }

    [Fact]
    public async Task Store_LegacyApprovedRequest_StillRunsTheCallItWasMadeFor()
    {
        // A request made before CON-185: the hash of the full arguments and nothing sealed.
        string tokenKey = $"pat:{Guid.NewGuid()}";
        var call = McpCallArguments.From(Arguments(new { body = new { Secret = $"legacy-{Guid.NewGuid():N}" } }));
        Guid id = await InsertLegacyAsync(tokenKey, call.Canonical, McpApprovalStatus.Approved, TimeSpan.FromMinutes(10));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>();

        var different = McpCallArguments.From(Arguments(new { body = new { Secret = "something-else" } }));
        (await store.TryConsumeAsync(tokenKey, "connapse_api_write", different.Canonical, different.Redacted)).Should().BeFalse();
        (await store.TryConsumeAsync(tokenKey, "connapse_api_write", call.Canonical, call.Redacted)).Should().BeTrue();
        var used = await RowAsync(id);
        used.Status.Should().Be(McpApprovalStatus.Used);
        used.ArgumentsHash.Should().BeEmpty("a used legacy request keeps no hash of its secret");
    }

    [Fact]
    public async Task Store_LegacyPendingRequest_IsTheOneTheSameCallWaitsOn()
    {
        string tokenKey = $"pat:{Guid.NewGuid()}";
        var call = McpCallArguments.From(Arguments(new { body = new { Password = $"legacy-{Guid.NewGuid():N}" } }));
        Guid id = await InsertLegacyAsync(tokenKey, call.Canonical, McpApprovalStatus.Pending, TimeSpan.FromHours(1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>();

        (await store.RequestAsync(tokenKey, AdminUserId(), "connapse_api_write", call.Canonical, call.Redacted)).Id.Should().Be(id);
    }

    [Fact]
    public async Task HideLegacySecrets_LegacyRows_HidesEverySecretAndLeavesPendingOnesApprovable()
    {
        // Run against a temporary table of the same shape, which Postgres resolves before the real one, so the
        // test touches no other test's requests.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE mcp_approvals (LIKE public.mcp_approvals INCLUDING ALL)");
        string pendingSecret = McpCallArguments.From(Arguments(new { operation = "connections.create", body = new { client_secret = "s1" } })).Canonical;
        string pendingPlain = McpCallArguments.From(Arguments(new { containerId = "c1" })).Canonical;
        string deniedSecret = McpCallArguments.From(Arguments(new { body = new { Password = "s2" } })).Canonical;
        string expiredSecret = McpCallArguments.From(Arguments(new { body = new { ApiKey = "s3" } })).Canonical;
        string approvedSecret = McpCallArguments.From(Arguments(new { body = new { Token = "s4" } })).Canonical;
        string usedSecret = McpCallArguments.From(Arguments(new { body = new { Passphrase = "s5" } })).Canonical;
        var rows = new (string Key, string Summary, string Status, int ExpiresInMinutes)[]
        {
            ("pending-secret", pendingSecret, "Pending", 60), ("pending-plain", pendingPlain, "Pending", 60),
            ("denied", deniedSecret, "Denied", 60), ("expired", expiredSecret, "Pending", -1),
            ("approved", approvedSecret, "Approved", 10), ("used", usedSecret, "Used", 10),
        };
        foreach (var (key, summary, status, minutes) in rows)
        {
            string hash = Sha256(summary);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO mcp_approvals (id, token_key, owner_user_id, tool_name, arguments_hash, arguments_summary, status, created_at, expires_at)
                VALUES ({Guid.NewGuid()}, {key}, {Guid.NewGuid()}, 'tool', {hash}, {summary}, {status}, now(), now() + make_interval(mins => {minutes}))
                """);
        }

        await db.Database.ExecuteSqlRawAsync(Connapse.Identity.Migrations.SealMcpApprovalArguments.HideLegacySecrets);

        var after = (await db.Database.SqlQueryRaw<LegacyRow>(
                """SELECT token_key AS "Key", arguments_hash AS "Hash", arguments_summary AS "Summary", status AS "Status" FROM mcp_approvals""").ToListAsync())
            .ToDictionary(r => r.Key);
        after.Should().HaveCount(rows.Length);
        string.Join("|", after.Values.Select(r => r.Summary)).Should().NotContainAny("s1", "s2", "s3", "s4", "s5");
        after["pending-plain"].Summary.Should().Be(pendingPlain, "a pending call with no secret still shows its arguments");
        after["pending-secret"].Summary.Should().StartWith("(hidden:");
        after["pending-secret"].Hash.Should().Be(Sha256(pendingSecret), "its hash and status are kept, so approving it still works");
        after["pending-secret"].Status.Should().Be("Pending");
        after["approved"].Hash.Should().Be(Sha256(approvedSecret), "an approved call can still run");
        foreach (string finished in new[] { "denied", "expired", "used" })
        {
            after[finished].Summary.Should().BeEmpty();
            after[finished].Hash.Should().BeEmpty("a request never matched again keeps no hash of its secrets");
        }
    }

    [Fact]
    public async Task Store_ExpiredLegacyRequest_LosesItsHashTheNextTimeAnyRequestIsMade()
    {
        var call = McpCallArguments.From(Arguments(new { body = new { Secret = $"expired-{Guid.NewGuid():N}" } }));
        Guid expired = await InsertLegacyAsync($"pat:{Guid.NewGuid()}", call.Canonical, McpApprovalStatus.Pending, TimeSpan.FromMinutes(-1));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpApprovalStore>();

        var unrelated = McpCallArguments.From(Arguments(new { containerId = Guid.NewGuid() }));
        await store.RequestAsync($"pat:{Guid.NewGuid()}", AdminUserId(), "container_delete", unrelated.Canonical, unrelated.Redacted);

        (await RowAsync(expired)).ArgumentsHash.Should().BeEmpty();
    }

    private sealed class LegacyRow
    {
        public string Key { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    private async Task<Guid> InsertLegacyAsync(string tokenKey, string arguments, McpApprovalStatus status, TimeSpan expiresIn)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        var row = new McpApprovalEntity
        {
            Id = Guid.NewGuid(),
            TokenKey = tokenKey,
            OwnerUserId = AdminUserId(),
            ToolName = "connapse_api_write",
            ArgumentsHash = Sha256(arguments),
            ArgumentsSummary = arguments,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            DecidedAt = status == McpApprovalStatus.Pending ? null : DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + expiresIn,
        };
        db.McpApprovals.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<McpApprovalEntity> RowAsync(Guid id)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        return await db.McpApprovals.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Dictionary<string, JsonElement> Arguments(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

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
