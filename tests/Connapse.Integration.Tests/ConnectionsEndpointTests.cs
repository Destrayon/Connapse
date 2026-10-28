using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The connection lifecycle over <c>/api/v1/connections</c> through the real host: administrators only,
/// the secret never returned, renames, pauses, resumes and deletes audited, and a delete refused while
/// sources use the connection.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class ConnectionsEndpointTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private const string Connections = "/api/v1/connections";
    private const string Secret = "connection-secret-value-456";
    private const string EditorEmail = "editor@connections-endpoint-tests.connapse.io";
    private const string EditorPassword = "EditorTest1!";
    // An administrator who isn't the Owner: the fixture's own administrator is the instance's Owner.
    private const string AdminEmail = "admin@connections-endpoint-tests.connapse.io";

    public async Task InitializeAsync()
    {
        await AddUserAsync(EditorEmail, "Editor");
        await AddUserAsync(AdminEmail, "Admin");
    }

    private async Task AddUserAsync(string email, string role)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        if (await users.FindByEmailAsync(email) is not null)
            return;
        var user = new ConnapseUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            CreatedAt = DateTime.UtcNow,
        };
        (await users.CreateAsync(user, EditorPassword)).Succeeded.Should().BeTrue();
        await users.AddToRoleAsync(user, role);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpClient> EditorClientAsync() => ClientAsync(EditorEmail);

    private async Task<HttpClient> ClientAsync(string email)
    {
        using var anon = fixture.Factory.CreateClient();
        var response = await anon.PostAsJsonAsync("/api/v1/auth/token", new { email, password = EditorPassword });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();

        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
        return client;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    private async Task<Connection> CreateAsync(string name, string config = """{"host":"sftp.example.com","port":22}""",
        ConnectionProvider provider = ConnectionProvider.Sftp)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest(name, provider, config, Secret), null);
    }

    private async Task<Guid> AddSourceAsync(Guid connectionId, string name)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(
            new CreateSourceRequest(name, connectionId, """{"path":"/"}"""));
        return source.Id;
    }

    private async Task<List<string>> AuditActionsFor(Guid connectionId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await using var db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
        string id = connectionId.ToString();
        return await db.AuditLogs.Where(a => a.ResourceId == id).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToListAsync();
    }

    [Fact]
    public async Task EveryRoute_NonAdmin_Returns403()
    {
        using var editor = await EditorClientAsync();
        Guid id = (await CreateAsync(Unique("editor-refused"))).Id;

        (await editor.GetAsync(Connections)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.GetAsync($"{Connections}/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PatchAsJsonAsync($"{Connections}/{id}", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsync($"{Connections}/{id}/pause", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync($"{Connections}/{id}/resume", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.DeleteAsync($"{Connections}/{id}?confirmName=x")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PutAsJsonAsync($"{Connections}/{id}/secret", new { privateKey = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.PostAsJsonAsync($"{Connections}/{id}/test", new { })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.DeleteAsync($"{Connections}/{id}/host-key")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await editor.GetAsync($"{Connections}/{id}/discover")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AuditActionsFor(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Test_SftpSettingsForAnotherServerWithoutAKey_Returns400WithoutUsingTheStoredKey()
    {
        Connection created = await CreateAsync(Unique("test-other"),
            """{"host":"sftp.example.com","port":22,"username":"reader","allowedRoot":"/data"}""");

        var refused = await fixture.AdminClient.PostAsJsonAsync($"{Connections}/{created.Id}/test",
            new { config = new { host = "attacker.example.com", port = 22, username = "reader", allowedRoot = "/data" } });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("field").GetString().Should().Be("privateKey");
        body.GetProperty("detail").GetString().Should().Contain("reader@sftp.example.com:22");
        (await fixture.AdminClient.PostAsJsonAsync($"{Connections}/{Guid.NewGuid()}/test", new { }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Discover_ListsWhatAConnectionReaches_RefusingAProviderWithNothingToList()
    {
        Connection gitHub = await CreateAsync(Unique("discover-gh"), """{"installationId":1,"account":"acme"}""", ConnectionProvider.GitHub);
        Connection sftp = await CreateAsync(Unique("discover-sftp"));

        var found = await fixture.AdminClient.GetAsync($"{Connections}/{gitHub.Id}/discover");
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await found.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.TryGetProperty("outcome", out _).Should().BeTrue();
        body.Should().NotContain(Secret);

        var refused = await fixture.AdminClient.GetAsync($"{Connections}/{sftp.Id}/discover");
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("provider");
        (await fixture.AdminClient.GetAsync($"{Connections}/{Guid.NewGuid()}/discover")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForgetHostKey_RemovesOnlyThePinnedKeyAndAudits()
    {
        Connection created = await CreateAsync(Unique("host-key"),
            """{"host":"sftp.example.com","port":22,"username":"reader","allowedRoot":"/data","hostKeyFingerprint":"SHA256:pinned"}""");

        var response = await fixture.AdminClient.DeleteAsync($"{Connections}/{created.Id}/host-key");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var config = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("config");
        config.TryGetProperty("hostKeyFingerprint", out _).Should().BeFalse();
        config.GetProperty("host").GetString().Should().Be("sftp.example.com");
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().GetSecretAsync(created.Id)).Should().Be(Secret);
        (await AuditActionsFor(created.Id)).Should().Equal("connection.host_key_forgotten");
        (await fixture.AdminClient.DeleteAsync($"{Connections}/{Guid.NewGuid()}/host-key"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReplaceSecret_Owner_ReplacesTheKeyKeepingTheSettings_NeverReturningIt()
    {
        Connection created = await CreateAsync(Unique("secret"),
            """{"host":"sftp.example.com","port":22,"username":"reader","allowedRoot":"/data"}""");
        const string newKey = "replaced-connection-key-789";

        var response = await fixture.AdminClient.PutAsJsonAsync($"{Connections}/{created.Id}/secret", new { privateKey = newKey });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(newKey);
        var body = JsonDocument.Parse(raw).RootElement;
        body.GetProperty("config").GetProperty("host").GetString().Should().Be("sftp.example.com");
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().GetSecretAsync(created.Id)).Should().Contain(newKey);
        (await AuditActionsFor(created.Id)).Should().Equal("connection.credentials_replaced");

        var missing = await fixture.AdminClient.PutAsJsonAsync($"{Connections}/{created.Id}/secret", new { passphrase = "p" });
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("privateKey");
        (await fixture.AdminClient.PutAsJsonAsync($"{Connections}/{Guid.NewGuid()}/secret", new { privateKey = newKey }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReplaceSecret_AdminWhoIsNotTheOwner_ReplacesItOverEveryRoute()
    {
        using var admin = await ClientAsync(AdminEmail);
        Connection created = await CreateAsync(Unique("not-owner"),
            """{"host":"sftp.example.com","port":22,"username":"reader","allowedRoot":"/data"}""");

        (await admin.PutAsJsonAsync($"{Connections}/{created.Id}/secret", new { privateKey = "admin-put-key" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().GetSecretAsync(created.Id)).Should().Contain("admin-put-key");
        (await admin.PatchAsJsonAsync($"{Connections}/{created.Id}", new { privateKey = "admin-patch-key" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().GetSecretAsync(created.Id)).Should().Contain("admin-patch-key");
        // The Atlassian alias admits the administrator too; this connection isn't a site, so there is nothing to replace.
        (await admin.PutAsJsonAsync($"/api/v1/atlassian/sites/{created.Id}/credentials", new { clientId = "c", clientSecret = "s" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditActionsFor(created.Id)).Should().Contain("connection.credentials_replaced");
    }

    [Fact]
    public async Task ListAndGet_ReturnTheConnectionsSettings_NeverItsSecret()
    {
        Connection created = await CreateAsync(Unique("listed"));

        var listed = await fixture.AdminClient.GetAsync($"{Connections}?take=200&provider=Sftp");
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        string raw = await listed.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().OnlyContain(c => c.GetProperty("provider").GetString() == "Sftp");
        items.Should().Contain(c => c.GetProperty("id").GetGuid() == created.Id);

        var read = await fixture.AdminClient.GetAsync($"{Connections}/{created.Id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        string one = await read.Content.ReadAsStringAsync();
        one.Should().NotContain(Secret);
        var body = JsonDocument.Parse(one).RootElement;
        body.GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        body.GetProperty("config").GetProperty("host").GetString().Should().Be("sftp.example.com");

        (await fixture.AdminClient.GetAsync($"{Connections}/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await fixture.AdminClient.GetAsync($"{Connections}?take=0")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_RenamesAndAudits_RefusingABlankOrTakenName()
    {
        Connection created = await CreateAsync(Unique("rename"));
        Connection other = await CreateAsync(Unique("rename-taken"));
        string renamed = Unique("renamed");

        var response = await fixture.AdminClient.PatchAsJsonAsync($"{Connections}/{created.Id}", new { name = renamed });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().Should().Be(renamed);
        (await AuditActionsFor(created.Id)).Should().Contain("connection.updated");

        var blank = await fixture.AdminClient.PatchAsJsonAsync($"{Connections}/{created.Id}", new { name = " " });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await blank.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("name");
        (await fixture.AdminClient.PatchAsJsonAsync($"{Connections}/{created.Id}", new { name = other.Name }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_ChecksTheSettingsLikeThePage_StoresTheKeyWithoutReturningIt()
    {
        string name = Unique("created");
        var response = await fixture.AdminClient.PostAsJsonAsync(Connections, new
        {
            name,
            provider = "Sftp",
            config = new { host = "sftp.example.com", port = 2222, username = "reader", allowedRoot = "/data" },
            privateKey = Secret,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        string raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(Secret);
        var connection = JsonDocument.Parse(raw).RootElement.GetProperty("connection");
        Guid id = connection.GetProperty("id").GetGuid();
        response.Headers.Location!.ToString().Should().Be($"{Connections}/{id}");
        connection.GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        connection.GetProperty("config").GetProperty("port").GetInt32().Should().Be(2222);
        (await AuditActionsFor(id)).Should().Equal("connection.created");

        (await fixture.AdminClient.PostAsJsonAsync(Connections, new { name, provider = "Sftp", config = new { } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("Sftp", "config")]
    [InlineData("Atlassian", "provider")]
    [InlineData(null, "provider")]
    public async Task Create_SettingsThePageWouldRefuse_Returns400NamingTheField(string? provider, string field)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync(Connections,
            new { name = Unique("refused"), provider, config = new { host = "sftp.example.com" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().EndWith("#invalid-connection");
        body.GetProperty("field").GetString().Should().Be(field);
    }

    [Fact]
    public async Task Create_NonAdmin_Returns403AndStoresNothing()
    {
        using var editor = await EditorClientAsync();
        string name = Unique("editor-create");

        (await editor.PostAsJsonAsync(Connections, new { name, provider = "Sftp", config = new { } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = fixture.Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().ListAsync(0, int.MaxValue))
            .Should().NotContain(c => c.Name == name);
    }

    [Fact]
    public async Task UpdateSettings_ChecksThemAndKeepsTheStoredSecret()
    {
        Connection created = await CreateAsync(Unique("settings"));

        var response = await fixture.AdminClient.PatchAsJsonAsync($"{Connections}/{created.Id}",
            new { config = new { host = "sftp2.example.com", username = "reader", allowedRoot = "/data" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("name").GetString().Should().Be(created.Name);
        body.GetProperty("config").GetProperty("host").GetString().Should().Be("sftp2.example.com");
        using (var scope = fixture.Factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IConnectionStore>().GetSecretAsync(created.Id)).Should().Be(Secret);

        var refused = await fixture.AdminClient.PatchAsJsonAsync($"{Connections}/{created.Id}", new { config = new { host = "x" } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("config");
    }

    [Fact]
    public async Task PauseAndResume_DisableAndReEnableTheSources()
    {
        Connection created = await CreateAsync(Unique("pause"));
        (await fixture.AdminClient.PostAsync($"{Connections}/{created.Id}/pause", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "there is nothing to pause");
        Guid first = await AddSourceAsync(created.Id, Unique("pause-a"));
        Guid second = await AddSourceAsync(created.Id, Unique("pause-b"));

        var paused = await fixture.AdminClient.PostAsync($"{Connections}/{created.Id}/pause", null);
        paused.StatusCode.Should().Be(HttpStatusCode.OK);
        (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sources").GetInt32().Should().Be(2);

        (await fixture.AdminClient.PostAsJsonAsync($"{Connections}/{created.Id}/resume", new { sourceIds = new[] { Guid.NewGuid() } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await fixture.AdminClient.PostAsJsonAsync($"{Connections}/{created.Id}/resume", new { sourceIds = new[] { first } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = fixture.Factory.Services.CreateScope();
        var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        (await sources.GetAsync(first))!.Enabled.Should().BeTrue();
        (await sources.GetAsync(second))!.Enabled.Should().BeFalse();
        (await AuditActionsFor(created.Id)).Should().Contain(["connection.paused", "connection.resumed"]);
    }

    [Fact]
    public async Task Delete_NeedsTheName_IsRefusedWhileSourcesUseIt_ThenDeletesAndAudits()
    {
        Connection created = await CreateAsync(Unique("delete"));
        string sourceName = Unique("delete-src");
        Guid source = await AddSourceAsync(created.Id, sourceName);

        var wrong = await fixture.AdminClient.DeleteAsync($"{Connections}/{created.Id}?confirmName=nope");
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString().Should().Be("confirmName");

        var refused = await fixture.AdminClient.DeleteAsync($"{Connections}/{created.Id}?confirmName={created.Name}");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should().EndWith("#connection-in-use");
        body.GetProperty("sources")[0].GetProperty("url").GetString().Should().Be($"/sources/{source}");
        body.GetProperty("detail").GetString().Should().Contain(sourceName);

        using (var scope = fixture.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISourceStore>().DeleteAsync(source);

        (await fixture.AdminClient.DeleteAsync($"{Connections}/{created.Id}?confirmName={created.Name}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await fixture.AdminClient.GetAsync($"{Connections}/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AuditActionsFor(created.Id)).Should().Contain("connection.deleted");
    }
}
