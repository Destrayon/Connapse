using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Web.Components.Connections;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class AtlassianConnectionFormTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "super-secret-value";

    private readonly BunitContext ctx = new();
    private readonly StubAtlassianHandler stub = new();
    private readonly IConnectionStore store = Substitute.For<IConnectionStore>();
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();

    public AtlassianConnectionFormTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization();
        ctx.Services.AddLogging();

        // The site answers tenant_info, and Atlassian's token endpoint refuses every secret.
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(stub));

        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton(audit);
        ctx.Services.AddSingleton(new AtlassianSiteService(http, new AtlassianConnectionTester(http), store, audit,
            new Connapse.Storage.Connectors.Atlassian.AtlassianTokenSource(http, TimeProvider.System)));
    }

    public void Dispose() => ctx.Dispose();

    private static Connection AtlassianConnection(string name = "acme.atlassian.net") => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://{{name}}","cloudId":"{{CloudId}}","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);

    private void FillAndSave(IRenderedComponent<AtlassianConnectionForm> cut)
    {
        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-client-id").Input("client-1");
        cut.Find("#atlassian-client-secret").Change(Secret);
        cut.Find("#atlassian-save").Click();
    }

    [Fact]
    public void New_ShowsServiceAccountGuidance_WithConfluenceScopesUnderTheirOwnHeading()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        var guide = cut.Find("#atlassian-service-account-guide");
        guide.InnerHtml.Should().Contain("https://admin.atlassian.com");
        guide.TextContent.Should().Contain("Service accounts")
            .And.Contain("Global permissions")
            .And.Contain("Confluence Administrator")
            .And.Contain("view access")
            .And.Contain("Create credentials");
        cut.Find("#atlassian-site-intro").TextContent
            .Should().Contain("This service account reads your site for Atlassian sources and checks each user's access.");
        cut.FindAll("#atlassian-scopes-confluence code").Select(c => c.TextContent)
            .Should().Equal(AtlassianConnectionForm.ConfluenceScopeList.Split(' '));
        cut.FindAll("#atlassian-copy-all").Should().ContainSingle();
        AtlassianConnectionForm.ConfluenceScopeList.Should().Be(
            "read:space:confluence read:page:confluence read:comment:confluence read:attachment:confluence "
            + "read:folder:confluence read:content-details:confluence read:content.permission:confluence");
    }

    [Fact]
    public void Resolve_ShowsTheCloudId()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-resolve").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-cloud-id").TextContent.Should().Be(CloudId));
    }

    [Fact]
    public void Save_WhenAProbeFails_ShowsTheStepMessageAndSavesNothing()
    {
        bool created = false;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.OnCreated, (Connection _) => created = true));

        FillAndSave(cut);

        cut.WaitForAssertion(() =>
        {
            cut.Find("#atlassian-save-error").TextContent
                .Should().Contain("Atlassian rejected the client ID or secret.")
                .And.Contain("token");
        });
        store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        created.Should().BeFalse();
        cut.Find("#atlassian-save-error").TextContent.Should().NotContain(Secret);
    }

    [Fact]
    public void Save_WhenEveryProbePasses_CreatesTheConnectionAndClearsTheSecret()
    {
        stub.AcceptCredentials = true;
        Connection site = AtlassianConnection();
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(site);
        Connection? created = null;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.OnCreated, (Connection c) => created = c));

        FillAndSave(cut);

        cut.WaitForAssertion(() => created.Should().Be(site));
        store.Received(1).CreateAsync(
            Arg.Is<CreateConnectionRequest>(r => r.Provider == ConnectionProvider.Atlassian && r.Secret == Secret),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        AssertSecretGone(cut);
    }

    [Fact]
    public void Save_WhenAProbeFails_KeepsTheSecretOutOfStateAndMarkup_EvenWhenShown()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-error").TextContent.Should().Contain("rejected"));
        AssertSecretGone(cut);

        cut.Find("button[aria-controls='atlassian-client-secret']").Click();
        AssertSecretGone(cut);
        cut.Find("#atlassian-client-secret").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Save_WhenTheSiteIsAlreadySaved_KeepsTheSecretOutOfStateAndMarkup()
    {
        stub.AcceptCredentials = true;
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<Connection>(_ => throw new InvalidOperationException("exists"));
        var cut = ctx.Render<AtlassianConnectionForm>();

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-error").TextContent.Should().Contain("already exists"));
        AssertSecretGone(cut);

        cut.Find("button[aria-controls='atlassian-client-secret']").Click();
        AssertSecretGone(cut);
        cut.Find("#atlassian-client-secret").GetAttribute("value").Should().BeNullOrEmpty();
    }

    private static void AssertSecretGone(IRenderedComponent<AtlassianConnectionForm> cut)
    {
        cut.Markup.Should().NotContain(Secret);
        var fields = typeof(AtlassianConnectionForm).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        foreach (var field in fields.Where(f => f.FieldType == typeof(string)))
            ((string?)field.GetValue(cut.Instance)).Should().NotBe(Secret, $"field {field.Name} must not hold the secret");
    }

    [Fact]
    public void Existing_ShowsTheSiteWithoutAnyEditableField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        cut.Find("#atlassian-site-details").TextContent.Should()
            .Contain("https://acme.atlassian.net").And.Contain(CloudId).And.Contain("client-1");
        cut.FindAll("#atlassian-site-url, #atlassian-client-id, #atlassian-client-secret, #atlassian-save").Should().BeEmpty();
    }

    [Fact]
    public void TestAgain_RunsTheProbesWithTheStoredSecretAndShowsTheFailingStep()
    {
        Connection site = AtlassianConnection();
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        store.GetSecretAsync(site.Id, Arg.Any<CancellationToken>()).Returns(Secret);

        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));
        cut.Find("#atlassian-retest").Click();

        cut.WaitForAssertion(() =>
            cut.Find("#atlassian-retest-result").TextContent.Should()
                .Contain("Atlassian rejected the client ID or secret.").And.Contain("token"));
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
