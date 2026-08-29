using System.Net;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>
/// Disconnecting an AWS identity link must revoke the refresh token at Cognito before removing the
/// local row, and must remove the row regardless of whether revocation succeeded — see
/// <see cref="AwsIdentityLinkService"/>.
/// </summary>
[Trait("Category", "Unit")]
public class AwsIdentityLinkServiceTests
{
    private static readonly CognitoSettings ConfiguredSettings = new()
    {
        IssuerUrl = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_test",
        Domain = "https://my-pool.auth.us-east-1.amazoncognito.com",
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        Region = "us-east-1",
    };

    private AwsIdentityLinkStore CreateStore(string dbName) =>
        new(CreateFactory(dbName), new EphemeralDataProtectionProvider(), TimeProvider.System);

    private static IDbContextFactory<ConnapseIdentityDbContext> CreateFactory(string dbName)
    {
        var factory = Substitute.For<IDbContextFactory<ConnapseIdentityDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ConnapseIdentityDbContext(
                new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
                    .UseInMemoryDatabase(dbName)
                    .Options)));
        return factory;
    }

    // Bypasses AwsIdentityLinkStore.SaveAsync (which always writes a properly protected token) to
    // put a row in place whose ProtectedRefreshToken is not valid Data Protection payload at all —
    // the shape a corrupted column or a rotated-beyond-retention key ring would leave behind.
    private static async Task SeedUnreadableLinkAsync(string dbName, Guid userId, string email)
    {
        await using var db = new ConnapseIdentityDbContext(
            new DbContextOptionsBuilder<ConnapseIdentityDbContext>().UseInMemoryDatabase(dbName).Options);

        db.UserAwsIdentityLinks.Add(new UserAwsIdentityLinkEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            ProtectedRefreshToken = "not-valid-protected-data",
            ConnectedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static IOptionsMonitor<CognitoSettings> Options(CognitoSettings settings)
    {
        var options = Substitute.For<IOptionsMonitor<CognitoSettings>>();
        options.CurrentValue.Returns(settings);
        return options;
    }

    private static IHttpClientFactory HttpFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        return factory;
    }

    [Fact]
    public async Task DisconnectAsync_ExistingLink_RevokesAtCognitoWhileTheRowStillExists_ThenDeletesIt()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, "user", "user@example.com", "refresh-token-abc");

        bool? linkPresentDuringRevoke = null;
        var handler = new RecordingHandler(HttpStatusCode.OK,
            onSend: async () => linkPresentDuringRevoke = await store.GetAsync(userId) is not null);

        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(userId);

        result.Deleted.Should().BeTrue();
        result.RevokedSuccessfully.Should().BeTrue();
        linkPresentDuringRevoke.Should().BeTrue("the token must be revoked before the local row disappears");
        (await store.GetAsync(userId)).Should().BeNull("the row must be gone once Disconnect returns");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://my-pool.auth.us-east-1.amazoncognito.com/oauth2/revoke");
        handler.LastRequestBody.Should().Contain("token=refresh-token-abc");
        handler.LastRequestBody.Should().Contain("client_id=test-client-id");
        handler.LastRequestBody.Should().Contain("client_secret=test-client-secret");
    }

    [Fact]
    public async Task DisconnectAsync_RevocationFails_StillDeletesTheRow_AndReportsRevocationFailed()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, "user", "user@example.com", "refresh-token-abc");

        var handler = new RecordingHandler(HttpStatusCode.BadRequest);
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(userId);

        // A user who clicks Disconnect must end up disconnected locally regardless of what AWS
        // reports — but a failed revocation must not be reported as a clean success.
        result.Deleted.Should().BeTrue();
        result.RevokedSuccessfully.Should().BeFalse();
        (await store.GetAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DisconnectAsync_UnreachablePool_StillDeletesTheRow_AndReportsRevocationFailed()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, "user", "user@example.com", "refresh-token-abc");

        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(userId);

        result.Deleted.Should().BeTrue();
        result.RevokedSuccessfully.Should().BeFalse();
        (await store.GetAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DisconnectAsync_NoLinkExists_ReturnsDeletedFalse_AndNeverCallsCognito()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(Guid.NewGuid());

        result.Deleted.Should().BeFalse();
        handler.LastRequest.Should().BeNull("there is no token to revoke when nothing was connected");
    }

    [Fact]
    public async Task DisconnectAsync_UnreadableToken_ReportsRevocationFailed_NeverCallsCognito_ButStillDeletesTheRow()
    {
        // A row exists — unlike DisconnectAsync_NoLinkExists — but its token cannot be decrypted.
        // This must not be treated as "nothing to revoke": the row being present means the token
        // may still be live at Cognito, and Connapse has simply lost the ability to speak for it.
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await SeedUnreadableLinkAsync(dbName, userId, "user@example.com");
        var store = CreateStore(dbName);

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(userId);

        result.RevokedSuccessfully.Should().BeFalse(
            "an unreadable token means Connapse never told Cognito, not that there was nothing to tell it");
        handler.LastRequest.Should().BeNull("there is no plaintext token to send in a revoke request");
        result.Deleted.Should().BeTrue("the local row must still go regardless of the revoke outcome");
        (await store.GetAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DisconnectAsync_LinkReplacedDuringRevoke_LeavesTheNewRowInPlace_AndReportsLinkChanged()
    {
        // A reconnect races this disconnect: SaveAsync updates the existing row in place, keeping
        // its Id, while the HTTP revoke call for the *old* token is still in flight. An Id-based
        // delete would not be able to tell the new row apart from the old one and would remove the
        // reconnect's link along with it — the row must survive, and the caller must be told the
        // link changed so it can try again, rather than being told it cleanly disconnected.
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, "user", "user@example.com", "refresh-token-abc");

        var handler = new RecordingHandler(HttpStatusCode.OK,
            onSend: () => store.SaveAsync(userId, "reconnected", "reconnected@example.com", "refresh-token-xyz"));

        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var result = await sut.DisconnectAsync(userId);

        result.RevokedSuccessfully.Should().BeTrue("the original token was successfully revoked at Cognito");
        result.Deleted.Should().BeFalse("the row that exists now is not the one this call revoked");
        result.LinkChangedDuringDisconnect.Should().BeTrue();

        var survivingLink = await store.GetAsync(userId);
        survivingLink.Should().NotBeNull("the reconnect's link must survive an unrelated disconnect");
        survivingLink!.Email.Should().Be("reconnected@example.com");
    }

    [Fact]
    public async Task GetAsync_ExistingLink_ReturnsEmailAndTimestamps()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);
        var userId = Guid.NewGuid();
        await store.SaveAsync(userId, "user", "user@example.com", "refresh-token-abc");

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var dto = await sut.GetAsync(userId);

        dto.Should().NotBeNull();
        dto!.Email.Should().Be("user@example.com");
        dto.LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_NoLink_ReturnsNull()
    {
        var dbName = Guid.NewGuid().ToString();
        var store = CreateStore(dbName);

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sut = new AwsIdentityLinkService(
            store, Options(ConfiguredSettings), HttpFactory(handler), NullLogger<AwsIdentityLinkService>.Instance);

        var dto = await sut.GetAsync(Guid.NewGuid());

        dto.Should().BeNull();
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, Func<Task>? onSend = null) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (onSend is not null)
                await onSend();

            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
