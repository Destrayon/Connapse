using System.Security.Claims;
using System.Security.Cryptography;
using Bunit;
using Connapse.Application.Links;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Profile page's connected accounts with a stored GitHub App whose key can't be decrypted, as after the server's
/// encryption keys change: the page still loads and says GitHub sign-in is unavailable until an administrator enters
/// the key again.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public sealed class ProfileIntegrationsGitHubKeyTests : IDisposable
{
    private static readonly GitHubAppRegistration App =
        new(42, "connapse-test", "Iv1.client", "octo-org", "https://github.com/apps/connapse-test");

    private readonly BunitContext ctx = new();
    private readonly Guid userId = Guid.NewGuid();
    private readonly GitHubIdentityLinkStore links;

    public ProfileIntegrationsGitHubKeyTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("alice");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        ctx.Services.AddLogging();
        ctx.Services.AddOptions();

        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);
        credentials.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>())
            .Returns<GitHubAppCredentialMaterial?>(_ => throw new ProviderCredentialUnavailableException("github", new CryptographicException("The key ring is gone.")));
        ctx.Services.AddSingleton(credentials);
        ctx.Services.AddSingleton(sp => new ConnapseGitHubApp(
            sp.GetRequiredService<IServiceScopeFactory>(), Substitute.For<IHttpClientFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnapseGitHubApp>.Instance));

        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        ctx.Services.AddSingleton(authorizer);

        IDbContextFactory<ConnapseIdentityDbContext> db = new InMemoryIdentityDb(
            new DbContextOptionsBuilder<ConnapseIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        ctx.Services.AddSingleton(db);
        ctx.Services.AddSingleton(TimeProvider.System);
        links = new GitHubIdentityLinkStore(db, TimeProvider.System);
        ctx.Services.AddSingleton(links);
        ctx.Services.AddSingleton<EnforcementMigration>();
        ctx.Services.AddScoped<ListAccountLinks>();
        ctx.Services.AddScoped<UnlinkAccount>();
        ctx.Services.AddScoped<AccountLinkOperations>();
        // Everything else the page reaches is for the other providers, which these tests never touch.
        SubstituteMissingDependencies(ctx.Services);
        ctx.Services.AddFallbackServiceProvider(new Substitutes());
    }

    /// <summary>A substitute for each Connapse interface a registered service takes and nobody registered.</summary>
    private static void SubstituteMissingDependencies(IServiceCollection services)
    {
        var registered = services.Select(d => d.ServiceType).ToHashSet();
        var pending = new Queue<Type>(services.Select(d => d.ImplementationType).OfType<Type>());
        while (pending.TryDequeue(out var type))
        {
            if (type.IsGenericTypeDefinition)
                continue;
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                var needed = parameter.ParameterType;
                if (registered.Contains(needed) || needed.Namespace?.StartsWith("Connapse", StringComparison.Ordinal) != true)
                    continue;
                registered.Add(needed);
                if (needed.IsInterface)
                {
                    services.AddSingleton(needed, Substitute.For([needed], []));
                }
                else if (needed.IsClass && !needed.IsAbstract)
                {
                    services.AddScoped(needed);
                    pending.Enqueue(needed);
                }
            }
        }
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<ProfileIntegrations> Render()
    {
        var cut = ctx.Render<ProfileIntegrations>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("GitHub"));
        return cut;
    }

    [Fact]
    public void Render_StoredKeyUnreadable_LoadsAndShowsGitHubSignInUnavailable()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(
            "GitHub sign-in is not available: this deployment's GitHub App key can't be read. Ask an administrator to enter it again under Providers → GitHub."));
        cut.Markup.Should().NotContain("Continue to GitHub sign-in");
    }

    [Fact]
    public async Task Render_StoredKeyUnreadableAndGitHubLinked_StillShowsTheLinkAndDisconnect()
    {
        await links.SaveAsync(userId, 7, "octo");

        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("octo"));
        cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Disconnect"),
            "an unreadable key must not leave a linked account unremovable");
    }

    private sealed class InMemoryIdentityDb(DbContextOptions<ConnapseIdentityDbContext> options) : IDbContextFactory<ConnapseIdentityDbContext>
    {
        public ConnapseIdentityDbContext CreateDbContext() => new(options);
    }

    /// <summary>A substitute for any of Connapse's interfaces nobody registered; Blazor's own are left to Blazor.</summary>
    private sealed class Substitutes : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType.IsInterface && serviceType.Namespace?.StartsWith("Connapse", StringComparison.Ordinal) == true
                ? Substitute.For([serviceType], [])
                : null;
    }
}
