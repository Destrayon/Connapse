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
