using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Creating and updating custom sources over <c>/api/sources</c>: a custom source sits on a custom connection,
/// which holds the middle man's address and token (or names a provider that does), and holds no token itself.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomSourceEndpointsTests(SharedWebAppFixture fixture)
{
    private async Task<Connection> ContentOnlyConnectionAsync(string token = FakeMiddleMan.Token)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(new CreateConnectionRequest(
            $"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom, CustomConnectionConfig.ForMiddleMan(FakeMiddleMan.BaseUrl), token), null);
    }

    private async Task<Connection> ProviderConnectionAsync(string providerUrl = FakeMiddleMan.BaseUrl)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        CustomProvider provider = await scope.ServiceProvider.GetRequiredService<ICustomProviderStore>().CreateAsync(
            new SaveCustomProviderRequest($"p-{Guid.NewGuid():N}"[..20], providerUrl, FakeMiddleMan.Token, 100, true, null));
        return await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id)), null);
    }

    private static object Body(Guid connectionId, string scopeId = FakeMiddleMan.Scope, string? baseUrl = null, string? secret = null) => new
    {
        name = $"custom-{Guid.NewGuid():N}"[..20],
        connectionId,
        scopeJson = baseUrl is null ? JsonSerializer.Serialize(new { scopeId }) : JsonSerializer.Serialize(new { baseUrl, scopeId }),
        secret,
    };

    private async Task<Source> StoredAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        Guid id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(id))!;
    }

    private static async Task ExpectRefusal(HttpResponseMessage response, HttpStatusCode status, string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("field").GetString().Should().Be(field);
    }

    [Fact]
    public async Task Create_OnAContentOnlyConnection_StoresItsMiddleManAndNoToken()
    {
        Connection connection = await ContentOnlyConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id));

        (await response.Content.ReadAsStringAsync()).Should().NotContain(FakeMiddleMan.Token);
        Source source = await StoredAsync(response);
        source.ConnectionId.Should().Be(connection.Id);
        source.Provider.Should().BeNull();
        source.HasSecret.Should().BeFalse("a custom source reads with its connection's token");
        CustomSourceScope.TryParse(source.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public async Task Create_NamingTheConnectionsOwnBaseUrl_IsAccepted()
    {
        Connection connection = await ContentOnlyConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources",
            Body(connection.Id, baseUrl: FakeMiddleMan.BaseUrl + "/"));

        CustomSourceScope.TryParse((await StoredAsync(response)).ScopeJson)!.BaseUrl.Should().Be(FakeMiddleMan.BaseUrl);
    }

    [Fact]
    public async Task Create_NamingAnotherBaseUrl_IsRefused()
    {
        Connection connection = await ContentOnlyConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources",
            Body(connection.Id, baseUrl: "https://attacker.example"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "baseUrl");
    }

    [Fact]
    public async Task Create_WithASecret_IsRefused()
    {
        Connection connection = await ContentOnlyConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id, secret: FakeMiddleMan.Token));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
    }

    [Fact]
    public async Task Create_CustomSourceWithoutAConnection_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new
        {
            name = $"custom-{Guid.NewGuid():N}"[..20],
            provider = "Custom",
            scopeJson = JsonSerializer.Serialize(new { baseUrl = FakeMiddleMan.BaseUrl, scopeId = FakeMiddleMan.Scope }),
        });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "connectionId");
    }

    [Fact]
    public async Task Create_OnAConnectionWhoseTokenTheMiddleManRefuses_IsRefused()
    {
        Connection connection = await ContentOnlyConnectionAsync(token: "wrong");

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "connectionId");
    }

    [Fact]
    public async Task Create_ForAScopeTheMiddleManDoesNotHave_IsRefused()
    {
        Connection connection = await ContentOnlyConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id, scopeId: "nope"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "scopeId");
    }

    [Fact]
    public async Task Create_WhoseMiddleManIsDown_Answers502()
    {
        Connection connection = await ContentOnlyConnectionAsync();
        fixture.CustomSource.FailNextWith = HttpStatusCode.InternalServerError;

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id));

        await ExpectRefusal(response, HttpStatusCode.BadGateway, "baseUrl");
    }

    [Fact]
    public async Task Create_OnAProviderConnection_ReadsTheProvidersMiddleMan()
    {
        Connection connection = await ProviderConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id));

        Source source = await StoredAsync(response);
        source.ConnectionId.Should().Be(connection.Id);
        source.HasSecret.Should().BeFalse();
        CustomSourceScope.TryParse(source.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public async Task Create_MiddleManEchoingAnEncodedToken_DoesNotReturnIt()
    {
        const string token = "ab+cd/ef";
        Connection connection = await ContentOnlyConnectionAsync(token);
        fixture.CustomSource.AcceptAnyToken = true;
        fixture.CustomSource.FailNextWith = HttpStatusCode.InternalServerError;
        fixture.CustomSource.FailNextTitle = $"bad bearer {Uri.EscapeDataString(token)}";
        try
        {
            HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id));

            string body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(Uri.EscapeDataString(token)).And.NotContain(token).And.NotContain("bad bearer");
        }
        finally
        {
            fixture.CustomSource.AcceptAnyToken = false;
        }
    }

    private async Task<Guid> CreatedOnAConnectionAsync()
    {
        Connection connection = await ContentOnlyConnectionAsync();
        return (await StoredAsync(await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(connection.Id)))).Id;
    }

    [Fact]
    public async Task Update_CustomSourceScope_IsRefusedSoTheConnectionsTokenCannotBeRedirected()
    {
        Guid id = await CreatedOnAConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new
        {
            scopeJson = JsonSerializer.Serialize(new { baseUrl = "https://attacker.example", scopeId = FakeMiddleMan.Scope }),
        });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "scope");
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        Source source = (await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(id))!;
        CustomSourceScope.TryParse(source.ScopeJson)!.BaseUrl.Should().Be(FakeMiddleMan.BaseUrl);
    }

    [Fact]
    public async Task Update_CustomSourceSecret_IsRefusedAndPointsAtTheConnection()
    {
        Guid id = await CreatedOnAConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new { secret = FakeMiddleMan.Token });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
        (await response.Content.ReadAsStringAsync()).Should().Contain("on its connection");
    }

    [Fact]
    public async Task Update_SecretOnASourceThatIsNotCustom_IsRefused()
    {
        Source source;
        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            IConnectionStore connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
            Connection s3 = await connections.CreateAsync(new CreateConnectionRequest(
                $"s3-{Guid.NewGuid():N}"[..20], ConnectionProvider.S3, """{"bucketName":"b","region":"us-east-1"}"""), null);
            source = await scope.ServiceProvider.GetRequiredService<ISourceStore>().CreateAsync(new CreateSourceRequest(
                $"s3src-{Guid.NewGuid():N}"[..20], s3.Id, "{}"));
        }

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{source.Id}", new { secret = "anything" });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
    }
}
