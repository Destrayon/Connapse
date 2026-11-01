using System.Net;
using System.Security.Cryptography;
using Bunit;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Pages;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Providers page with a stored GitHub App whose key can't be decrypted, as after the server's encryption keys
/// change: the page still loads, says the key has to be entered again, and saves the one entered.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public sealed class ProvidersPageGitHubKeyTests : IDisposable
{
    private static readonly GitHubAppRegistration App =
        new(42, "connapse-test", "Iv1.client", "octo-org", "https://github.com/apps/connapse-test");

    private readonly BunitContext ctx = new();
    private readonly IProviderCredentialStore credentials = Substitute.For<IProviderCredentialStore>();
    private bool keyReadable;

    public ProvidersPageGitHubKeyTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();
        ctx.Services.AddOptions();

        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportRSAPrivateKeyPem();
        credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(App);
        credentials.GetGitHubAppMaterialAsync(Arg.Any<CancellationToken>()).Returns(_ => keyReadable
            ? new GitHubAppCredentialMaterial(App, pem, "secret")
            : throw new ProviderCredentialUnavailableException("github", new CryptographicException("The key ring is gone.")));
        credentials.SaveGitHubAppAsync(
                Arg.Any<GitHubAppRegistration>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                keyReadable = true;
                return new ProviderCredentialInfo("github", "connapse-test", DateTime.UtcNow, null);
            });
        ctx.Services.AddSingleton(credentials);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new GitHubStub()));
        // The stub answers whatever address it is sent, so nothing leaves the test.
        ctx.Services.AddSingleton(sp => new ConnapseGitHubApp(
            sp.GetRequiredService<IServiceScopeFactory>(), http, Microsoft.Extensions.Logging.Abstractions.NullLogger<ConnapseGitHubApp>.Instance));

        var setups = Substitute.For<IProviderSetupReader>();
        setups.ReadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new ProviderSetup("github", "GitHub",
                [new ProviderRequirement("GitHub App", "", RequirementStatus.Failed, "The stored private key can't be read.")],
                InUse: true),
        ]);
        ctx.Services.AddSingleton(setups);
        ctx.Services.AddSingleton(Substitute.For<IConnectionStore>());
        ctx.Services.AddSingleton(Substitute.For<ISourceStore>());
        ctx.Services.AddSingleton<GitHubAppUsage>();
        ctx.Services.AddSingleton(Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>());
        ctx.Services.AddSourceOperations();
        // Everything else the page injects is for the other providers, which these tests never touch.
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

    private IRenderedComponent<Providers> RenderGitHub()
    {
        var cut = ctx.Render<Providers>(p => p.Add(x => x.Key, "github"));
        cut.WaitForElement("#github-key-unreadable");
        return cut;
    }

    [Fact]
    public void Render_StoredKeyUnreadable_LoadsAndAsksForTheKeyAgain()
    {
        var cut = RenderGitHub();

        cut.Find("#github-key-unreadable").TextContent.Should()
            .Contain("connapse-test").And.Contain("can't be read").And.Contain("App ID 42");
        cut.Markup.Should().Contain("Not available: add the App's client secret", "the secret can't be read either");
        cut.Find("#github-app-id").GetAttribute("value").Should().Be("42", "the App is still known, only its key is not");
    }

    [Fact]
    public void Save_StoredKeyUnreadable_SavesTheKeyEnteredAgain()
    {
        var cut = RenderGitHub();
        using var rsa = RSA.Create(2048);
        string newKey = rsa.ExportRSAPrivateKeyPem();

        cut.Find("#github-app-key").Change(newKey);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Check and save")).Click();

        cut.WaitForAssertion(() => cut.FindAll("#github-key-unreadable").Should().BeEmpty());
        credentials.Received(1).SaveGitHubAppAsync(
            Arg.Is<GitHubAppRegistration>(a => a.AppId == 42), newKey, null, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        cut.Markup.Should().Contain("GitHub App connapse-test saved.");
    }

    /// <summary>GitHub, answering "who is this App" for any key.</summary>
    private sealed class GitHubStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/app"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":42,"slug":"connapse-test","client_id":"Iv1.client","owner":{"login":"octo-org"},"html_url":"https://github.com/apps/connapse-test"}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
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
