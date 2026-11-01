using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Bunit;
using Connapse.Application.Ports;
using Connapse.Application.Providers;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Providers;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>Easy setup for the GitHub App: GitHub's create form, and replacing a stored App through it.</summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class GitHubAppEasySetupTests : IDisposable
{
    private static readonly GitHubAppRegistration Stored = new(42, "connapse-old", "Iv1.client", "octo", "https://github.com/apps/connapse-old");

    private readonly BunitContext ctx = new();
    private readonly IProviderCredentialStore credentials = Substitute.For<IProviderCredentialStore>();
    /// <summary>Held answers: each GitHub lookup waits on the next gate, if one is queued.</summary>
    private readonly ConcurrentQueue<TaskCompletionSource> gates = new();
    private bool gitHubUp = true;

    public GitHubAppEasySetupTests()
    {
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new GitHubUsers(() => gitHubUp, gates)));
        var scopes = new ServiceCollection().AddSingleton(credentials).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var gitHub = new ConnapseGitHubApp(scopes, http, NullLogger<ConnapseGitHubApp>.Instance);

        ctx.Services.AddSingleton(new StartGitHubAppCreation(
            new GitHubManifestRequests(new MemoryCache(new MemoryCacheOptions())), gitHub, credentials, authorizer));
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<GitHubAppEasySetup> Render(GitHubAppRegistration? app, bool replace = false)
    {
        credentials.GetGitHubAppAsync(Arg.Any<CancellationToken>()).Returns(app);
        return ctx.Render<GitHubAppEasySetup>(p => p.Add(x => x.App, app).Add(x => x.ReplaceConfirmed, replace));
    }

    private static void Tick(IRenderedComponent<GitHubAppEasySetup> cut, bool replace) =>
        cut.Render(p => p.Add(x => x.ReplaceConfirmed, replace));

    private static void UseOrganisation(IRenderedComponent<GitHubAppEasySetup> cut, string org)
    {
        cut.Find("#github-app-org-toggle").Click();
        cut.Find("#github-app-org").Input(org);
        cut.Find("#github-app-org-check").Click();
    }

    [Fact]
    public void NoStoredApp_OffersTheCreateForm()
    {
        var cut = Render(app: null);

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeFalse());
        cut.Find("#github-app-create-form").GetAttribute("action").Should().StartWith("https://github.com/settings/apps/new?state=");
        cut.Find("#github-app-create").TextContent.Should().Contain("Create the App on GitHub");
    }

    [Fact]
    public void StoredApp_ReplaceNotTicked_ShowsTheFormDisabledAndSaysToTickReplace()
    {
        var cut = Render(Stored);

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeTrue());
        cut.Find("#github-app-create-help").TextContent.Should().Contain("Tick \"Replace connapse-old with a different App\" above first");
    }

    [Fact]
    public void StoredApp_ReplaceTicked_StartsANewCreationThatMayReplaceIt()
    {
        var cut = Render(Stored);
        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeTrue());

        Tick(cut, replace: true);

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeFalse());
        cut.Find("#github-app-create-form").GetAttribute("action").Should().StartWith("https://github.com/settings/apps/new?state=");
        cut.Find("#github-app-create").TextContent.Should().Contain("Create a new App on GitHub");
        cut.Find("#github-app-create-help").TextContent.Should().NotContain("Tick");
    }

    [Fact]
    public void StoredApp_ReplaceUnticked_DisablesTheFormAgain()
    {
        var cut = Render(Stored, replace: true);
        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeFalse());

        Tick(cut, replace: false);

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeTrue());
    }

    [Fact]
    public void StoredApp_Organisation_IsConfirmedWithoutApiTextThenReplacedOnceTicked()
    {
        var cut = Render(Stored);
        UseOrganisation(cut, "octo-org");

        cut.WaitForAssertion(() => cut.Find("#github-app-create-help").TextContent.Should().Contain("Owned by octo-org"));
        cut.FindAll("#github-app-org-error").Should().BeEmpty();
        cut.Markup.Should().NotContain("replaceExisting");
        cut.Find("#github-app-create").HasAttribute("disabled").Should().BeTrue();

        Tick(cut, replace: true);

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeFalse());
        cut.Find("#github-app-create-form").GetAttribute("action").Should().StartWith("https://github.com/organizations/octo-org/settings/apps/new?state=");
    }

    [Fact]
    public void Organisation_GitHubDownWhenReplaceIsTicked_SaysSoInsteadOfOfferingTheForm()
    {
        var cut = Render(Stored);
        UseOrganisation(cut, "octo-org");
        cut.WaitForAssertion(() => cut.Find("#github-app-create-help").TextContent.Should().Contain("Owned by octo-org"));

        gitHubUp = false;
        Tick(cut, replace: true);

        cut.WaitForAssertion(() => cut.Find("#github-app-org-error").TextContent.Should().NotBeEmpty());
        cut.FindAll("#github-app-create").Should().BeEmpty();
    }

    [Fact]
    public void Organisation_ReplaceUntickedWhileGitHubIsSlow_NeverLeavesAReplacingFormSubmittable()
    {
        var cut = Render(Stored);
        UseOrganisation(cut, "octo-org");
        cut.WaitForAssertion(() => cut.Find("#github-app-create-help").TextContent.Should().Contain("Owned by octo-org"));

        Tick(cut, replace: true);
        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeFalse());

        var held = new TaskCompletionSource();
        gates.Enqueue(held);
        Tick(cut, replace: false);
        cut.WaitForAssertion(() => gates.Should().BeEmpty());
        cut.FindAll("#github-app-create").Should().AllSatisfy(b => b.HasAttribute("disabled").Should().BeTrue());
        held.SetResult();
        cut.WaitForAssertion(() => cut.Find("#github-app-create-help").TextContent.Should().Contain("Tick"));

        // Ticked then unticked, with GitHub answering the ticked request last.
        var ticked = new TaskCompletionSource();
        var unticked = new TaskCompletionSource();
        gates.Enqueue(ticked);
        gates.Enqueue(unticked);
        Tick(cut, replace: true);
        cut.WaitForAssertion(() => gates.Should().HaveCount(1));
        Tick(cut, replace: false);
        cut.WaitForAssertion(() => gates.Should().BeEmpty());
        unticked.SetResult();
        cut.WaitForAssertion(() => cut.Find("#github-app-create-help").TextContent.Should().Contain("Tick"));
        ticked.SetResult();

        cut.WaitForAssertion(() => cut.Find("#github-app-create").HasAttribute("disabled").Should().BeTrue());
        cut.Find("#github-app-create-form").HasAttribute("action").Should().BeFalse();
    }

    [Fact]
    public void Organisation_ThatIsAPerson_IsRefusedWithGitHubsAnswer()
    {
        var cut = Render(app: null);
        UseOrganisation(cut, "a-person");

        cut.WaitForAssertion(() => cut.Find("#github-app-org-error").TextContent.Should().Contain("a-person is a personal account"));
        cut.FindAll("#github-app-create").Should().BeEmpty();
    }

    /// <summary>GitHub's anonymous account lookup, the only call starting a creation makes.</summary>
    private sealed class GitHubUsers(Func<bool> up, ConcurrentQueue<TaskCompletionSource> gates) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (gates.TryDequeue(out var gate))
                await gate.Task.WaitAsync(ct);
            string? body = request.RequestUri!.AbsolutePath switch
            {
                "/users/octo-org" => """{"login":"octo-org","type":"Organization"}""",
                "/users/a-person" => """{"login":"a-person","type":"User"}""",
                _ => null,
            };
            return !up() ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
