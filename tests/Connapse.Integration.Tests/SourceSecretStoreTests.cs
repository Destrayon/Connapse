using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>A content-only custom source keeps its middle man's token encrypted beside the source.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourceSecretStoreTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task CreateAsync_WithSecret_StoresItEncryptedAndReturnsItOnRequest()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore store = scope.ServiceProvider.GetRequiredService<ISourceStore>();
        const string token = "middle-man-token-5c1e";

        Source source = await store.CreateAsync(new CreateSourceRequest(
            $"custom-secret-{Guid.NewGuid():N}",
            ConnectionId: null,
            ScopeJson: new CustomSourceScope("https://bridge.acme.test", "eng").ToJson(),
            Provider: ConnectionProvider.Custom,
            Secret: token));

        source.HasSecret.Should().BeTrue();
        (await store.GetAsync(source.Id))!.HasSecret.Should().BeTrue();
        (await store.GetSecretAsync(source.Id)).Should().Be(token);

        IDbContextFactory<KnowledgeDbContext> contexts =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await contexts.CreateDbContextAsync();
        string? stored = await context.Sources.Where(s => s.Id == source.Id).Select(s => s.SecretProtected).SingleAsync();
        stored.Should().NotBeNullOrEmpty().And.NotContain(token);
    }

    [Fact]
    public async Task CreateAsync_WithoutSecret_HasNone()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISourceStore store = scope.ServiceProvider.GetRequiredService<ISourceStore>();

        Source source = await store.CreateAsync(new CreateSourceRequest(
            $"custom-nosecret-{Guid.NewGuid():N}",
            ConnectionId: null,
            ScopeJson: new CustomSourceScope("https://bridge.acme.test", "eng").ToJson(),
            Provider: ConnectionProvider.Custom));

        source.HasSecret.Should().BeFalse();
        (await store.GetSecretAsync(source.Id)).Should().BeNull();
    }
}
