using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Connapse.Integration.Tests;

/// <summary>
/// Revoking an OAuth client ends every refresh already in flight (CON-189). A refresh used to read the token, then
/// save an unrevoked replacement with no check, so a revoke landing in between was undone; a client known by its
/// metadata URL, which has no registration to delete, kept refreshing. Each test orders the two with gates on
/// the refresh's database commands, never with sleeps.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OAuthClientRevokeRaceTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Refresh_RevokeCommitsAfterTheTokenWasRead_IssuesNoWorkingToken()
    {
        string clientId = MetadataClientId();
        string refreshToken = await SeedClientTokenAsync(clientId);
        var gate = new PauseAfterFirstCommand();
        await using var refreshDb = CreateDb(gate);

        var refresh = RefreshAsync(refreshDb, refreshToken);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (await RevokeAsync(clientId, "revoke-after-read")).Should().NotBeNull("the client held a refresh token");
        gate.Release.SetResult();

        (await refresh.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeNull("the token it read was revoked before it saved");
        await ShouldHoldNoWorkingTokenAsync(clientId, refreshed: null);
    }

    [Fact]
    public async Task Refresh_RevokeStartsBeforeTheReplacementCommits_RevokesTheReplacement()
    {
        string clientId = MetadataClientId();
        string refreshToken = await SeedClientTokenAsync(clientId);
        var gate = new PauseBeforeCommit();
        await using var refreshDb = CreateDb(gate);

        var refresh = RefreshAsync(refreshDb, refreshToken);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        string revokeName = $"con189-{Guid.NewGuid():N}";
        var revoke = RevokeAsync(clientId, revokeName);
        await RevokeBlockedOrDoneAsync(revoke, revokeName);
        gate.Release.SetResult();

        var refreshed = await refresh.WaitAsync(TimeSpan.FromSeconds(30));
        await revoke.WaitAsync(TimeSpan.FromSeconds(30));
        await ShouldHoldNoWorkingTokenAsync(clientId, refreshed?.RefreshToken);
    }

    private static string MetadataClientId() => $"https://mcp-{Guid.NewGuid():N}.example.com/client.json";

    private string ConnectionString(string? applicationName = null) =>
        new NpgsqlConnectionStringBuilder(fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection"))
        {
            ApplicationName = applicationName,
        }.ConnectionString;

    private ConnapseIdentityDbContext CreateDb(IInterceptor? interceptor = null, string? applicationName = null)
    {
        var options = new DbContextOptionsBuilder<ConnapseIdentityDbContext>().UseNpgsql(ConnectionString(applicationName));
        if (interceptor is not null)
            options.AddInterceptors(interceptor);
        return new ConnapseIdentityDbContext(options.Options);
    }

    private Task<Connapse.Core.TokenResponse?> RefreshAsync(ConnapseIdentityDbContext db, string refreshToken) => Task.Run(() =>
        new JwtTokenService(fixture.Factory.Services.GetRequiredService<IOptionsMonitor<JwtSettings>>(), db,
            NullLogger<JwtTokenService>.Instance).RefreshTokenAsync(refreshToken, "http://localhost"));

    private Task<Connapse.Core.OAuthClientRevocation?> RevokeAsync(string clientId, string applicationName) => Task.Run(() =>
        new OAuthClientRegistry(new Factory(() => CreateDb(applicationName: applicationName)), TimeProvider.System).RevokeAsync(clientId));

    /// <summary>Waits until the revoke is waiting on a lock the paused refresh holds, or has finished without one.</summary>
    private async Task RevokeBlockedOrDoneAsync(Task revoke, string applicationName)
    {
        await using var db = CreateDb();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!revoke.IsCompleted)
        {
            int waiting = await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE application_name = {applicationName} AND wait_event_type = 'Lock'").SingleAsync();
            if (waiting > 0)
                return;
            DateTime.UtcNow.Should().BeBefore(deadline, "the revoke should block on the refresh or finish");
            await Task.Yield();
        }
    }

    private async Task<string> SeedClientTokenAsync(string clientId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = AdminUserId();
        string refreshToken = (await scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateTokenPairAsync(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], userId)).RefreshToken;
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        var row = await db.RefreshTokens.SingleAsync(t => t.TokenHash == Hash(refreshToken));
        row.ClientId = clientId;
        row.Scope = "knowledge:read";
        await db.SaveChangesAsync();
        return refreshToken;
    }

    private async Task ShouldHoldNoWorkingTokenAsync(string clientId, string? refreshed)
    {
        await using (var db = CreateDb())
        {
            (await db.RefreshTokens.Where(t => t.ClientId == clientId && t.RevokedAt == null).CountAsync())
                .Should().Be(0, "a revoked client holds no unrevoked refresh token");
        }

        if (refreshed is null)
            return;
        using var anonymous = fixture.Factory.CreateClient();
        using var response = await anonymous.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshed,
            ["client_id"] = clientId,
        }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the replacement was revoked with its client");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid" or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        Guid.TryParse(sub, out Guid id).Should().BeTrue();
        return id;
    }

    private sealed class Factory(Func<ConnapseIdentityDbContext> create) : IDbContextFactory<ConnapseIdentityDbContext>
    {
        public ConnapseIdentityDbContext CreateDbContext() => create();
    }

    /// <summary>Holds the refresh after its first command, the read of the token, until released.</summary>
    private sealed class PauseAfterFirstCommand : DbCommandInterceptor
    {
        private int _commands;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync();
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync();
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            await PauseAsync();
            return result;
        }

        private async Task PauseAsync()
        {
            if (Interlocked.Increment(ref _commands) != 2)
                return;
            Reached.SetResult();
            await Release.Task;
        }
    }

    /// <summary>Holds the refresh just before it commits its replacement token, until released.</summary>
    private sealed class PauseBeforeCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task;
            return result;
        }
    }
}
