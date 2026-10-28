using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Application.Connections;
using Connapse.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Audit rows written by a Blazor page action name the person who acted. A page action runs in
/// the circuit's scope, where the authentication state is set but there is no request of its own,
/// so the user must come from the circuit rather than from the HTTP context.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class AuditActorIntegrationTests(SharedWebAppFixture fixture)
{
    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c => c.Type is "sub" or "nameid"
            or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        return Guid.Parse(sub!);
    }

    [Fact]
    public async Task PageAction_InACircuitWithNoHttpContext_RecordsTheActingUser()
    {
        Guid admin = AdminUserId();
        string name = $"audit-actor-{Guid.NewGuid():N}"[..24];
        Guid connectionId;

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
            connectionId = (await connections.CreateAsync(
                new CreateConnectionRequest(name, ConnectionProvider.S3, """{"bucketName":"b","region":"us-east-1"}"""), null)).Id;
        }

        try
        {
            await using (var circuit = fixture.Factory.Services.CreateAsyncScope())
            {
                // What Blazor does when a circuit starts: the signed-in user becomes the scope's state.
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, admin.ToString()), new Claim(ClaimTypes.Role, "Owner")], "Cookies"));
                ((IHostEnvironmentAuthenticationStateProvider)circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>())
                    .SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
                circuit.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

                // The Connections page's Save, as a rename: connections.update as the circuit's user.
                var authentication = await circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
                var store = circuit.ServiceProvider.GetRequiredService<IConnectionStore>();
                var saved = await circuit.ServiceProvider.GetRequiredService<ConnectionOperations>().SaveAsync(authentication,
                    (await store.GetAsync(connectionId))!, name + "-r", null, _ => { }, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
                saved.Succeeded.Should().BeTrue(saved.Detail);
            }

            await using var read = fixture.Factory.Services.CreateAsyncScope();
            await using var db = await read.ServiceProvider
                .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync();
            string id = connectionId.ToString();
            var row = await db.AuditLogs.SingleAsync(a => a.ResourceId == id && a.Action == "connection.updated");
            row.UserId.Should().Be(admin);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().DeleteAsync(connectionId);
        }
    }
}
