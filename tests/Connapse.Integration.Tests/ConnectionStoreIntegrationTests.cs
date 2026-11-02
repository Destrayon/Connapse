using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ConnectionStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private static CreateConnectionRequest NewRequest(string? secret = "super-secret-key") =>
        new($"conn-{Guid.NewGuid():N}"[..24], ConnectionProvider.S3, """{"region":"us-east-1"}""", secret);

    [Fact]
    public async Task RecordHealthAsync_AFailureKeepsTheLastSuccessAndItsScopes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var created = await store.CreateAsync(NewRequest(), createdByUserId: null);
        var passedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        await store.RecordHealthAsync(created.Id, new ConnectionHealth(passedAt, Succeeded: true, Step: null, Message: "ok",
            Origin: "test", ConfirmedScopes: ["read:space:confluence"]));
        var failed = await store.RecordHealthAsync(created.Id, new ConnectionHealth(passedAt.AddHours(1), Succeeded: false,
            Step: "token", Message: "refused", Origin: "sync", CredentialRejected: true));

        failed!.LastSucceededAt.Should().Be(passedAt);
        failed.ConfirmedScopes.Should().Equal("read:space:confluence");
        var read = await store.GetAsync(created.Id);
        read!.Health.Should().BeEquivalentTo(failed);
        read.UpdatedAt.Should().BeCloseTo(created.UpdatedAt, TimeSpan.FromMilliseconds(1), "a check changes nothing an admin configured");
        (await store.ListAsync(0, 1000)).Single(c => c.Id == created.Id).Health!.CredentialRejected.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ExpectedVersionIsStale_ThrowsAndKeepsTheOtherSave()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var created = await store.CreateAsync(NewRequest("first-secret"), createdByUserId: null);
        var checkedAgainst = (await store.GetAsync(created.Id))!;

        await store.UpdateAsync(created.Id, new UpdateConnectionRequest(
            ConfigJson: """{"region":"eu-west-1"}""", Secret: "second-secret", ExpectedUpdatedAt: checkedAgainst.UpdatedAt));
        var stale = () => store.UpdateAsync(created.Id, new UpdateConnectionRequest(
            ConfigJson: """{"region":"us-west-2"}""", ExpectedUpdatedAt: checkedAgainst.UpdatedAt));

        await stale.Should().ThrowAsync<ConnectionChangedException>();
        (await store.GetAsync(created.Id))!.ConfigJson.Should().Contain("eu-west-1");
        (await store.GetSecretAsync(created.Id)).Should().Be("second-secret");
    }

    [Fact]
    public async Task VersionedReads_AfterAnotherSave_RefuseTheSecretAndWriteNoHealth()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var created = await store.CreateAsync(NewRequest("first-secret"), createdByUserId: null);
        var tested = (await store.GetAsync(created.Id))!;

        (await store.GetSecretAtAsync(created.Id, tested.UpdatedAt)).Should().Be("first-secret");
        var passed = new ConnectionHealth(DateTime.UtcNow, Succeeded: true, Step: null, Message: "ok", Origin: "test");
        (await store.RecordHealthAtAsync(created.Id, passed, tested.UpdatedAt))!.Succeeded.Should().BeTrue();
        (await store.GetAsync(created.Id))!.UpdatedAt.Should().Be(tested.UpdatedAt, "recording health is not a save");

        await store.UpdateAsync(created.Id, new UpdateConnectionRequest(ConfigJson: """{"region":"eu-west-1"}""", Secret: "second-secret"));
        var staleSecret = () => store.GetSecretAtAsync(created.Id, tested.UpdatedAt);
        var failed = passed with { Succeeded = false, Message = "refused" };

        await staleSecret.Should().ThrowAsync<ConnectionChangedException>();
        (await store.RecordHealthAtAsync(created.Id, failed, tested.UpdatedAt)).Should().BeNull();
        (await store.GetAsync(created.Id))!.Health!.Succeeded.Should().BeTrue();
        (await store.GetSecretAtAsync(Guid.NewGuid(), tested.UpdatedAt)).Should().BeNull();
    }

    [Fact]
    public async Task RecordHealthAsync_UnknownConnection_ReturnsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        (await store.RecordHealthAsync(Guid.NewGuid(), new ConnectionHealth(DateTime.UtcNow, true, null, null, "test")))
            .Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_WithSecret_DoesNotExposeSecretOnReadModel()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var created = await store.CreateAsync(NewRequest(), createdByUserId: null);

        created.HasSecret.Should().BeTrue();
        created.ConfigJson.Should().Contain("us-east-1");
    }

    [Fact]
    public async Task GetSecretAsync_AfterCreate_RoundTripsThePlaintext()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var created = await store.CreateAsync(NewRequest("round-trip-me"), createdByUserId: null);

        string? secret = await store.GetSecretAsync(created.Id);

        secret.Should().Be("round-trip-me");
    }

    [Fact]
    public async Task GetSecretAsync_WhenNoSecretStored_ReturnsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var request = new CreateConnectionRequest(
            $"fs-{Guid.NewGuid():N}"[..24], ConnectionProvider.Filesystem, """{"root":"/data"}""", Secret: null);
        var created = await store.CreateAsync(request, createdByUserId: null);

        string? secret = await store.GetSecretAsync(created.Id);

        secret.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var request = NewRequest();
        await store.CreateAsync(request, createdByUserId: null);

        Func<Task> act = async () => await store.CreateAsync(request, createdByUserId: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateAsync_WithNullSecret_LeavesExistingSecretIntact()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var created = await store.CreateAsync(NewRequest("keep-me"), createdByUserId: null);

        await store.UpdateAsync(created.Id, new UpdateConnectionRequest(Name: $"renamed-{Guid.NewGuid():N}"[..24]));

        string? secret = await store.GetSecretAsync(created.Id);
        secret.Should().Be("keep-me");
    }

    [Fact]
    public async Task DeleteAsync_WithNoSources_RemovesTheConnection()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionStore>();

        var created = await store.CreateAsync(NewRequest(), createdByUserId: null);

        bool deleted = await store.DeleteAsync(created.Id);

        deleted.Should().BeTrue();
        (await store.GetAsync(created.Id)).Should().BeNull();
    }
}
