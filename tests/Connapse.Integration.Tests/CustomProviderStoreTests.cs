using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>Custom providers: many per deployment, each with its own encrypted token.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomProviderStoreTests(SharedWebAppFixture fixture)
{
    private static SaveCustomProviderRequest Request(string? token = "provider-token", string? name = null) => new(
        name ?? $"bridge-{Guid.NewGuid():N}"[..20], "https://bridge.acme.test/api", token,
        MaxCheckBatch: 50, SupportsAccessKeys: true, LinkUrl: null);

    [Fact]
    public async Task Create_StoresTheTokenEncryptedAndKeepsItOnAnUpdateWithoutOne()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();

        CustomProvider created = await store.CreateAsync(Request());
        CustomProvider updated = (await store.UpdateAsync(created.Id, Request(token: null, name: created.Name) with { MaxCheckBatch = 10 }))!;

        updated.HasToken.Should().BeTrue();
        updated.MaxCheckBatch.Should().Be(10);
        updated.VerifiedAt.Should().NotBeNull();
        (await store.GetTokenAsync(created.Id)).Should().Be("provider-token");
        (await store.ListAsync()).Should().Contain(p => p.Id == created.Id);

        await using KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        string? stored = await context.CustomProviders.Where(p => p.Id == created.Id).Select(p => p.TokenProtected).SingleAsync();
        stored.Should().NotContain("provider-token");
    }

    [Fact]
    public async Task Create_SameNameTwice_IsRefused()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        CustomProvider first = await store.CreateAsync(Request());

        Func<Task> act = () => store.CreateAsync(Request(name: first.Name));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_WhileAConnectionUsesIt_IsRefused()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        IConnectionStore connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        CustomProvider provider = await store.CreateAsync(Request());
        Connection connection = await connections.CreateAsync(new CreateConnectionRequest(
            $"custom-conn-{Guid.NewGuid():N}"[..24], ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id)), null);

        Func<Task> act = () => store.DeleteAsync(provider.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connection*");
        await connections.DeleteAsync(connection.Id);
        (await store.DeleteAsync(provider.Id)).Should().BeTrue();
        (await store.GetAsync(provider.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Update_ChangingTheAddressWhileAConnectionUsesIt_IsRefused()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        CustomProvider provider = await store.CreateAsync(Request());
        await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(new CreateConnectionRequest(
            $"custom-conn-{Guid.NewGuid():N}"[..24], ConnectionProvider.Custom, CustomConnectionConfig.ToJson(provider.Id)), null);

        Func<Task> moved = () => store.UpdateAsync(provider.Id, Request(name: provider.Name) with { BaseUrl = "https://elsewhere.acme.test" });
        Func<Task> same = () => store.UpdateAsync(provider.Id, Request(name: provider.Name) with { MaxCheckBatch = 7 });

        await moved.Should().ThrowAsync<InvalidOperationException>().WithMessage("*address*");
        await same.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GenerateLinkSecret_StoresItEncryptedAndReplacesTheOldOne()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ICustomProviderStore store = scope.ServiceProvider.GetRequiredService<ICustomProviderStore>();
        CustomProvider provider = await store.CreateAsync(Request());
        (await store.GetLinkSecretAsync(provider.Id)).Should().BeNull();

        string? first = await store.GenerateLinkSecretAsync(provider.Id);
        string? second = await store.GenerateLinkSecretAsync(provider.Id);

        first.Should().NotBeNullOrEmpty().And.NotBe(second);
        (await store.GetLinkSecretAsync(provider.Id)).Should().Be(second);
        (await store.GetAsync(provider.Id))!.HasLinkSecret.Should().BeTrue();
        (await store.GenerateLinkSecretAsync(Guid.NewGuid())).Should().BeNull();
    }
}
