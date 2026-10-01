using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Web.Components.Providers;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class AtlassianProviderCardTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "super-secret-value";

    private readonly BunitContext ctx = new();
    private readonly StubAtlassianHandler stub = new();
    private readonly IConnectionStore store = Substitute.For<IConnectionStore>();
    private readonly IProviderCredentialStore credentials = Substitute.For<IProviderCredentialStore>();

    public AtlassianProviderCardTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization();
        ctx.Services.AddLogging();

        // The site answers tenant_info, and Atlassian's token endpoint refuses every secret.
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(stub));

        var audit = Substitute.For<IAuditLogger>();
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>([]));

        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton(credentials);
        ctx.Services.AddSingleton(new AtlassianSiteService(http, new AtlassianConnectionTester(http), store, audit,
            new Connapse.Storage.Connectors.Atlassian.AtlassianTokenSource(http, TimeProvider.System)));
    }

    public void Dispose() => ctx.Dispose();

    private static Connection AtlassianConnection(string name = "acme.atlassian.net") => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://{{name}}","cloudId":"{{CloudId}}","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    private void StoreHas(params Connection[] connections) =>
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Connection>>(connections));

    [Fact]
    public void Render_ShowsThreeStepsAndTheScopeList()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("section.provider-step h2").Select(h => h.TextContent)
            .Should().Equal("Linking app", "Add a site", "Saved sites");
        cut.Markup.Should().Contain("https://admin.atlassian.com")
            .And.Contain("Easy setup")
            .And.Contain("Manual values")
            .And.Contain("Confluence Administrator");
        foreach (string scope in AtlassianProviderCard.ScopeList.Split(' '))
            cut.Markup.Should().Contain(scope);
        AtlassianProviderCard.ScopeList.Should().Be(
            "read:space:confluence read:page:confluence read:comment:confluence read:attachment:confluence "
            + "read:folder:confluence read:content-details:confluence read:content.permission:confluence");
    }

    [Fact]
    public void LinkingApp_NotSetUp_ShowsTheConsoleLinkCallbackUrlAndScope()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        var step = cut.Find("#atlassian-app");
        step.TextContent.Should().Contain("Not set up")
            .And.Contain("User identity API").And.Contain("read:me")
            .And.Contain("Distribution").And.Contain("Sharing")
            .And.Contain("Manual values");
        step.InnerHtml.Should().Contain("https://developer.atlassian.com/console/myapps/");
        cut.Find("#atlassian-callback-url").TextContent.Should().Be("http://localhost/api/v1/auth/cloud/atlassian/callback");
        cut.FindAll("#atlassian-copy-callback").Should().ContainSingle();
    }

    [Fact]
    public void LinkingApp_Save_StoresTheAppAndNeverRendersTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client"));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.FindAll("#atlassian-app-saved").Should().ContainSingle());
        credentials.Received(1).SaveAtlassianLinkAppAsync("link-client", Secret, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app").TextContent.Should().Contain("Saved, waiting for a first sign-in");
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Verified_IsReady()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(new AtlassianLinkAppRegistration("link-client", DateTime.UtcNow));

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app").ClassList.Should().Contain("provider-step--satisfied");
        cut.Find("#atlassian-app").TextContent.Should().Contain("Ready");
    }

    [Fact]
    public void Resolve_ShowsTheCloudId()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-resolve").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-cloud-id").TextContent.Should().Be(CloudId));
    }

    [Fact]
    public void Save_WhenAProbeFails_ShowsTheStepMessageAndSavesNothing()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-client-id").Input("client-1");
        cut.Find("#atlassian-client-secret").Change(Secret);
        cut.Find("#atlassian-save").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("#atlassian-save-error").TextContent
                .Should().Contain("Atlassian rejected the client ID or secret.")
                .And.Contain("token");
        });
        store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        cut.Find("#atlassian-save-error").TextContent.Should().NotContain(Secret);
    }

    [Fact]
    public void Save_WhenEveryProbePasses_CreatesTheConnectionAndClearsTheSecret()
    {
        stub.AcceptCredentials = true;
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => AtlassianConnection());
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-client-id").Input("client-1");
        cut.Find("#atlassian-client-secret").Change(Secret);
        cut.Find("#atlassian-save").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Saved acme.atlassian.net."));
        store.Received(1).CreateAsync(
            Arg.Is<CreateConnectionRequest>(r => r.Provider == ConnectionProvider.Atlassian && r.Secret == Secret),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void SavedSites_AreListedWithTestAgain_AndOtherProvidersAreNot()
    {
        StoreHas(AtlassianConnection(), new Connection(
            Guid.NewGuid(), "my-bucket", ConnectionProvider.S3, null, null, DateTime.UtcNow, DateTime.UtcNow));

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("#atlassian-site-list li").Should().ContainSingle()
            .Which.TextContent.Should().Contain("acme.atlassian.net").And.Contain("Test again");
        cut.Markup.Should().NotContain("my-bucket");
    }

    [Fact]
    public void TestAgain_RunsTheProbesWithTheStoredSecretAndShowsTheResult()
    {
        Connection site = AtlassianConnection();
        StoreHas(site);
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        store.GetSecretAsync(site.Id, Arg.Any<CancellationToken>()).Returns(Secret);

        var cut = ctx.Render<AtlassianProviderCard>();
        cut.Find("#atlassian-site-list button").Click();

        cut.WaitForAssertion(() =>
            cut.Find("#atlassian-site-list li").TextContent.Should().Contain("Atlassian rejected the client ID or secret."));
        store.Received(1).GetSecretAsync(site.Id, Arg.Any<CancellationToken>());
        cut.Markup.Should().NotContain(Secret);
    }

    private sealed class StubAtlassianHandler : HttpMessageHandler
    {
        /// <summary>When true the token endpoint and every Confluence probe succeed, as for a correct admin account.</summary>
        public bool AcceptCredentials { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string? json = null;

            if (path.EndsWith("/_edge/tenant_info", StringComparison.Ordinal))
                json = $$"""{"cloudId":"{{CloudId}}"}""";
            else if (AcceptCredentials && path.EndsWith("/oauth/token", StringComparison.Ordinal))
                json = """{"access_token":"t","expires_in":3600}""";
            else if (AcceptCredentials && path.EndsWith("/rest/api/user/current", StringComparison.Ordinal))
                json = """{"accountId":"service"}""";
            else if (AcceptCredentials && path.EndsWith("/api/v2/spaces", StringComparison.Ordinal))
                json = """{"results":[{"id":"1"}]}""";
            else if (AcceptCredentials && path.EndsWith("/api/v2/pages", StringComparison.Ordinal))
                json = """{"results":[{"id":"10"}]}""";
            else if (AcceptCredentials && path.EndsWith("/rest/api/search/user", StringComparison.Ordinal))
                json = """{"results":[{"user":{"accountId":"someone-else"}}]}""";
            else if (AcceptCredentials && path.EndsWith("/permission/check", StringComparison.Ordinal))
                json = """{"hasPermission":true}""";

            return Task.FromResult(json is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
