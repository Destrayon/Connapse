using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Web.Components.Pages;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>The custom provider page: check the middle man in two steps, then save.</summary>
[Trait("Category", "Unit")]
public class CustomProviderPageTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly FakeMiddleMan remote = new();
    private readonly ICustomProviderStore store = Substitute.For<ICustomProviderStore>();
    private readonly List<SaveCustomProviderRequest> saved = [];

    public CustomProviderPageTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetRoles("Admin");
        ctx.Services.AddLogging();

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(remote, disposeHandler: false));
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        ctx.Services.AddSingleton(new CustomProviderSetup(http, settings));
        ctx.Services.AddSingleton(store);
        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([]));
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());

        store.CreateAsync(Arg.Any<SaveCustomProviderRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<SaveCustomProviderRequest>();
            saved.Add(request);
            return new CustomProvider(Guid.NewGuid(), request.Name, request.BaseUrl, true, request.MaxCheckBatch,
                request.SupportsAccessKeys, request.LinkUrl, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        });
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<CustomProviderPage> NewProvider(string token = FakeMiddleMan.Token)
    {
        var cut = ctx.Render<CustomProviderPage>();
        cut.Find("#custom-provider-name").Input("Acme bridge");
        cut.Find("#custom-provider-url").Input(FakeMiddleMan.BaseUrl);
        cut.Find("#custom-provider-token").Change(token);
        cut.Find("#custom-provider-check").Click();
        return cut;
    }

    [Fact]
    public void NewProvider_WorkingMiddleMan_PassesBothStepsAndSaves()
    {
        var cut = NewProvider();

        cut.WaitForAssertion(() => cut.Find("#custom-permissions").ClassList.Should().Contain("provider-step--satisfied"));
        cut.Find("#custom-endpoint").ClassList.Should().Contain("provider-step--satisfied");
        cut.Find("#custom-provider-save").Click();

        cut.WaitForAssertion(() => saved.Should().ContainSingle());
        saved[0].Should().BeEquivalentTo(new SaveCustomProviderRequest(
            "Acme bridge", FakeMiddleMan.BaseUrl, FakeMiddleMan.Token, 100, SupportsAccessKeys: true, LinkUrl: null));
    }

    [Fact]
    public void NewProvider_WrongToken_SaysSoAndCannotBeSaved()
    {
        var cut = NewProvider(token: "wrong");

        cut.WaitForAssertion(() => cut.Find("#custom-provider-token-error").TextContent.Should().Contain("refused the token"));
        cut.Find("#custom-provider-save").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void NewProvider_WithoutPermissionChecks_CannotBeSaved()
    {
        remote.SupportsPermissions = false;

        var cut = NewProvider();

        cut.WaitForAssertion(() => cut.Find("#custom-endpoint").ClassList.Should().Contain("provider-step--satisfied"));
        cut.Find("#custom-permissions").ClassList.Should().NotContain("provider-step--satisfied");
        cut.Find("#custom-provider-save").HasAttribute("disabled").Should().BeTrue();
    }

    private CustomProvider Saved(string? linkUrl, bool hasLinkSecret = false) => new(
        Guid.NewGuid(), "Acme bridge", FakeMiddleMan.BaseUrl, true, 100, true, linkUrl,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, hasLinkSecret);

    [Fact]
    public void SavedProviderOfferingLinking_ShowsTheValuesTheMiddleManNeeds()
    {
        CustomProvider provider = Saved("https://bridge.acme.test/link");
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));

        cut.Find("#custom-link-audience").TextContent.Should().Be(provider.Id.ToString());
        cut.Find("#custom-link-issuer").TextContent.Should().Be("http://localhost");
        cut.FindAll("#custom-link-secret").Should().BeEmpty();
    }

    [Fact]
    public void GenerateLinkSecret_ShowsItOnceAndMarksTheStepDone()
    {
        CustomProvider provider = Saved("https://bridge.acme.test/link");
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider, provider with { HasLinkSecret = true });
        store.GenerateLinkSecretAsync(provider.Id, Arg.Any<CancellationToken>()).Returns("s3cret-value");

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));
        cut.Find("#custom-link-generate").Click();

        cut.WaitForAssertion(() => cut.Find("#custom-link-secret").GetAttribute("value").Should().Be("s3cret-value"));
        cut.Find("#custom-linking").ClassList.Should().Contain("provider-step--satisfied");
        cut.Find("#custom-link-replace").TextContent.Should().Contain("Replace link secret");
        cut.Find("label[for=custom-link-secret]").TextContent.Should().Be("Link secret");
    }

    [Fact]
    public void ReplaceLinkSecret_AsksFirstAndSaysWhatItBreaks()
    {
        CustomProvider provider = Saved("https://bridge.acme.test/link", hasLinkSecret: true);
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));
        cut.FindAll("#custom-linking button").Single(b => b.TextContent.Contains("Show linking values")).Click();
        cut.Find("#custom-link-replace button").Click();

        cut.Find("#custom-link-replace").TextContent.Should().Contain("New links fail until your middle man has the new secret");
        store.DidNotReceive().GenerateLinkSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void NewProvider_WithoutAName_TakesTheNameTheMiddleManReports()
    {
        var cut = ctx.Render<CustomProviderPage>();
        cut.Find("#custom-provider-url").Input(FakeMiddleMan.BaseUrl);
        cut.Find("#custom-provider-token").Change(FakeMiddleMan.Token);

        cut.Find("#custom-provider-check").Click();

        cut.WaitForAssertion(() => cut.Find("#custom-provider-name").GetAttribute("value").Should().Be("Fake"));
        cut.Find("#custom-endpoint").TextContent.Should().Contain("Reachable, token accepted, wrong token refused");
    }

    [Fact]
    public void SavedProviderUsedByConnections_NamesThemAndCannotBeDeleted()
    {
        CustomProvider provider = Saved(linkUrl: null);
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);
        ctx.Services.GetRequiredService<IConnectionStore>().ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([new Connection(Guid.NewGuid(), "acme-perms", ConnectionProvider.Custom,
                CustomConnectionConfig.ToJson(provider.Id), null, DateTime.UtcNow, DateTime.UtcNow)]));

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));

        cut.Find("#custom-provider-dependants").TextContent.Should().Contain("acme-perms");
        cut.Find("#custom-provider-delete button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void SavedProvider_ShowsWhenItWasLastVerified()
    {
        CustomProvider provider = Saved(linkUrl: null);
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));

        cut.Find("#custom-endpoint").TextContent.Should().Contain("Verified");
    }

    [Fact]
    public void SavedProviderWithoutLinking_HasNoLinkingStep()
    {
        CustomProvider provider = Saved(linkUrl: null);
        store.GetAsync(provider.Id, Arg.Any<CancellationToken>()).Returns(provider);

        var cut = ctx.Render<CustomProviderPage>(p => p.Add(x => x.Id, provider.Id));

        cut.FindAll("#custom-linking").Should().BeEmpty();
    }
}
