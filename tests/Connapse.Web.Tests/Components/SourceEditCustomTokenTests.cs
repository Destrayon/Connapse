using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Connectors;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using SourceEditPage = Connapse.Web.Components.Pages.SourceEdit;

namespace Connapse.Web.Tests.Components;

/// <summary>A custom source's edit page: replacing its token after the middle man accepts the new one.</summary>
[Trait("Category", "Unit")]
public class SourceEditCustomTokenTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly FakeMiddleMan remote = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();

    private readonly Source source = new(
        Guid.NewGuid(), "eng", null, null, new CustomSourceScope(FakeMiddleMan.BaseUrl, FakeMiddleMan.Scope).ToJson(),
        DateTime.UtcNow, DateTime.UtcNow, Provider: ConnectionProvider.Custom, HasSecret: true);

    public SourceEditCustomTokenTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        sources.ReplaceSecretAsync(source.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(remote, disposeHandler: false));
        var settings = Substitute.For<IOptionsMonitor<CustomSourceSettings>>();
        settings.CurrentValue.Returns(new CustomSourceSettings());
        var connections = Substitute.For<IConnectionStore>();

        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IConnectorFactory>());
        ctx.Services.AddSingleton(audit);
        ctx.Services.AddSingleton(new CustomSourceTokenReplacement(sources, connections, new CustomSourceSetup(http, settings), audit));
        ctx.Services.AddLogging();
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<SourceEditPage> Render() => ctx.Render<SourceEditPage>(p => p.Add(x => x.Id, source.Id));

    [Fact]
    public void TokenTheMiddleManAccepts_ReplacesTheStoredOne()
    {
        var cut = Render();
        cut.Find("#source-token").Change(FakeMiddleMan.Token);
        cut.Find("#source-token-replace").Click();

        cut.Find("#source-token-confirm").Click();

        cut.WaitForAssertion(() => cut.Find("#source-token-replaced"));
        sources.Received(1).ReplaceSecretAsync(source.Id, FakeMiddleMan.Token, Arg.Any<CancellationToken>());
        audit.Received(1).LogAsync("source.secret_replaced", "source", source.Id.ToString(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TokenTheMiddleManRefuses_ShowsWhyAndKeepsTheOldOne()
    {
        var cut = Render();
        cut.Find("#source-token").Change("wrong");
        cut.Find("#source-token-replace").Click();

        cut.Find("#source-token-confirm").Click();

        cut.WaitForAssertion(() => cut.Find("#source-token-error"));
        cut.FindAll("#source-token-replaced").Should().BeEmpty();
        sources.DidNotReceive().ReplaceSecretAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void EmptyToken_IsRefusedWithoutAskingTheMiddleMan()
    {
        var cut = Render();

        cut.Find("#source-token-replace").Click();

        cut.WaitForAssertion(() => cut.Find("#source-token-error").TextContent.Should().Contain("Enter the token"));
        cut.FindAll("#source-token-confirm").Should().BeEmpty("there is nothing to confirm without a token");
        remote.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Page_SaysWhetherATokenIsStored_AndCancelLeavesItAlone()
    {
        var cut = Render();
        cut.Find("#source-token-stored").TextContent.Should().Contain("A token is stored.");
        cut.Find("#source-token").Change(FakeMiddleMan.Token);
        cut.Find("#source-token-replace").Click();

        cut.Find("#source-token-cancel").Click();

        cut.FindAll("#source-token-confirm").Should().BeEmpty();
        sources.DidNotReceive().ReplaceSecretAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
