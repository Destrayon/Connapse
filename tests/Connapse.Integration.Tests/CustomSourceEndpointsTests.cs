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

/// <summary>Creating a content-only custom source over <c>POST /api/sources</c>.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomSourceEndpointsTests(SharedWebAppFixture fixture)
{
    private static object Body(
        string? baseUrl = FakeMiddleMan.BaseUrl,
        string scopeId = FakeMiddleMan.Scope,
        string? secret = FakeMiddleMan.Token,
        string provider = "Custom") => new
        {
            name = $"custom-{Guid.NewGuid():N}"[..20],
            provider,
            scopeJson = JsonSerializer.Serialize(new { baseUrl, scopeId }),
            secret,
        };

    [Fact]
    public async Task Create_ContentOnlyCustomSource_StoresItWithItsToken()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(baseUrl: FakeMiddleMan.BaseUrl + "/"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(FakeMiddleMan.Token);
        Guid id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore store = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        Source source = (await store.GetAsync(id))!;
        source.Provider.Should().Be(ConnectionProvider.Custom);
        source.ConnectionId.Should().BeNull();
        source.HasSecret.Should().BeTrue();
        CustomSourceScope.TryParse(source.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
        (await store.GetSecretAsync(id)).Should().Be(FakeMiddleMan.Token);
    }

    [Fact]
    public async Task Create_CustomSourceWithoutToken_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(secret: null));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
    }

    [Fact]
    public async Task Create_CustomSourceOverPlainHttp_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(baseUrl: "http://bridge.acme.test/api"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "baseUrl");
    }

    [Fact]
    public async Task Create_CustomSourceTheMiddleManRefusesTheTokenFor_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(secret: "wrong"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
    }

    [Fact]
    public async Task Create_CustomSourceForAScopeTheMiddleManDoesNotHave_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(scopeId: "nope"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "scopeId");
    }

    [Fact]
    public async Task Create_CustomSourceWhoseMiddleManIsDown_Answers502()
    {
        fixture.CustomSource.FailNextWith = HttpStatusCode.InternalServerError;

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body());

        await ExpectRefusal(response, HttpStatusCode.BadGateway, "baseUrl");
    }

    [Fact]
    public async Task Create_SecretForANonCustomSource_IsRefused()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(provider: "S3"));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
    }

    private static async Task ExpectRefusal(HttpResponseMessage response, HttpStatusCode status, string field)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, body);
        JsonDocument.Parse(body).RootElement.GetProperty("field").GetString().Should().Be(field);
    }

    [Fact]
    public async Task Update_CustomSourceScope_IsRefusedSoItsTokenCannotBeRedirected()
    {
        HttpResponseMessage created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body());
        Guid id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new
        {
            scopeJson = JsonSerializer.Serialize(new { baseUrl = "https://attacker.example", scopeId = FakeMiddleMan.Scope }),
        });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "scope");
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        Source source = (await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetAsync(id))!;
        CustomSourceScope.TryParse(source.ScopeJson)!.BaseUrl.Should().Be(FakeMiddleMan.BaseUrl);
    }

    private async Task<Connection> CustomConnectionAsync(string providerUrl = FakeMiddleMan.BaseUrl)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        CustomProvider provider = await scope.ServiceProvider.GetRequiredService<ICustomProviderStore>().CreateAsync(
            new SaveCustomProviderRequest($"p-{Guid.NewGuid():N}"[..20], providerUrl, FakeMiddleMan.Token, 100, true, null));
        return await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
            new CreateConnectionRequest($"c-{Guid.NewGuid():N}"[..20], ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id)), null);
    }

    private static object ConnectedBody(Guid connectionId, string baseUrl = FakeMiddleMan.BaseUrl) => new
    {
        name = $"custom-{Guid.NewGuid():N}"[..20],
        connectionId,
        scopeJson = JsonSerializer.Serialize(new { baseUrl, scopeId = FakeMiddleMan.Scope }),
        secret = FakeMiddleMan.Token,
    };

    [Fact]
    public async Task Create_CustomSourceOnACustomConnection_IsTheSameSourceWithItsConnectionFilledIn()
    {
        Connection connection = await CustomConnectionAsync();

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", ConnectedBody(connection.Id));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        Guid id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore store = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        Source source = (await store.GetAsync(id))!;
        source.ConnectionId.Should().Be(connection.Id);
        source.HasSecret.Should().BeTrue();
        CustomSourceScope.TryParse(source.ScopeJson).Should().Be(new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope));
    }

    [Fact]
    public async Task Create_ConnectedCustomSourceReadingAnotherMiddleMan_IsRefused()
    {
        Connection connection = await CustomConnectionAsync(providerUrl: "https://other-bridge.acme.test");

        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", ConnectedBody(connection.Id));

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "baseUrl");
    }

    [Fact]
    public async Task Update_ConnectedCustomSourceScope_IsRefused()
    {
        Connection connection = await CustomConnectionAsync();
        HttpResponseMessage created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", ConnectedBody(connection.Id));
        Guid id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new
        {
            scopeJson = JsonSerializer.Serialize(new { baseUrl = "https://attacker.example", scopeId = FakeMiddleMan.Scope }),
        });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "scope");
    }

    private async Task<Guid> ContentOnlySourceAsync(string token)
    {
        fixture.CustomSource.AcceptAnyToken = true;
        try
        {
            HttpResponseMessage created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", Body(secret: token));
            created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        }
        finally
        {
            fixture.CustomSource.AcceptAnyToken = false;
        }
    }

    private async Task<string?> StoredSecretAsync(Guid id)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISourceStore>().GetSecretAsync(id);
    }

    [Fact]
    public async Task Update_CustomSourceTokenTheMiddleManAccepts_ReplacesTheStoredOne()
    {
        Guid id = await ContentOnlySourceAsync("rotated-away-token");

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new { secret = FakeMiddleMan.Token });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().NotContain(FakeMiddleMan.Token);
        (await StoredSecretAsync(id)).Should().Be(FakeMiddleMan.Token);
    }

    [Fact]
    public async Task Update_CustomSourceTokenTheMiddleManRefuses_KeepsTheStoredOne()
    {
        Guid id = await ContentOnlySourceAsync("still-the-old-token");

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new { secret = "wrong" });

        await ExpectRefusal(response, HttpStatusCode.BadRequest, "secret");
        (await StoredSecretAsync(id)).Should().Be("still-the-old-token");
    }

    [Fact]
    public async Task Update_ConnectedCustomSourceToken_ReplacesIt()
    {
        Connection connection = await CustomConnectionAsync();
        HttpResponseMessage created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", ConnectedBody(connection.Id));
        Guid id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        HttpResponseMessage response = await fixture.AdminClient.PatchAsJsonAsync($"/api/sources/{id}", new { secret = $"  {FakeMiddleMan.Token}  " });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await StoredSecretAsync(id)).Should().Be(FakeMiddleMan.Token);
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
