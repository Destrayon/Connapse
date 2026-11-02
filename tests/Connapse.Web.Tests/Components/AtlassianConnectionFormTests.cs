using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Connections;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class AtlassianConnectionFormTests : IDisposable
{
    private const string CloudId = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "super-secret-value";
    private const string NewSecret = "brand-new-secret-value";

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
        var authorizer = Substitute.For<Connapse.Application.Ports.IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        var sites = new AtlassianSiteService(http, new AtlassianConnectionTester(http), store, audit,
            new Connapse.Storage.Connectors.Atlassian.AtlassianTokenSource(http, TimeProvider.System),
            new Connapse.Application.Connections.CreateConnection(store, Substitute.For<Connapse.Application.Ports.IConnectionSettingsRules>(),
                audit, authorizer),
            new Connapse.Application.Connections.ReplaceConnectionSecret(store, Substitute.For<ISourceStore>(),
                Substitute.For<Connapse.Application.Ports.IConnectionSettingsRules>(), audit, authorizer));
        ctx.Services.AddSingleton(sites);

        // connections.test runs an Atlassian site's test through the site service, as the real runner does.
        var runner = Substitute.For<Connapse.Application.Ports.IConnectionTestRunner>();
        runner.TestAsync(Arg.Any<Connapse.Application.Ports.ConnectionTestSubject>(), Arg.Any<CancellationToken>())
            .Returns(c => sites.TestStoredAsync(c.Arg<Connapse.Application.Ports.ConnectionTestSubject>().Stored!.Id));
        ctx.Services.AddSingleton(new Connapse.Application.Connections.AtlassianSiteChecks(
            new Connapse.Application.Connections.ResolveAtlassianSite(http, authorizer),
            new Connapse.Application.Connections.TestAtlassianSite(new Connapse.Application.Connections.TestConnection(store, runner, authorizer))));
    }

    public void Dispose() => ctx.Dispose();

    private static Connection AtlassianConnection(string name = "acme.atlassian.net", ConnectionHealth? health = null) => new(
        Guid.NewGuid(), name, ConnectionProvider.Atlassian,
        $$"""{"siteUrl":"https://{{name}}","cloudId":"{{CloudId}}","clientId":"client-1"}""",
        null, DateTime.UtcNow, DateTime.UtcNow, HasSecret: true, SourceCount: 2, Health: health);

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
            .And.Contain("Select app roles")
            .And.Contain("admin group")
            .And.Contain("view access")
            .And.Contain("Create credentials");
        cut.Find("#atlassian-site-intro").TextContent
            .Should().Contain("This service account reads your site's Confluence spaces for Confluence sources and checks each user's access.");
        cut.FindAll("#atlassian-scopes-confluence code").Select(c => c.TextContent)
            .Should().Equal(AtlassianConnectionForm.ConfluenceScopeList.Split(' '));
        cut.FindAll("#atlassian-copy-required").Should().ContainSingle();
        cut.FindAll("#atlassian-copy-with-attachments").Should().ContainSingle();
        AtlassianConnectionForm.ConfluenceScopeList.Should().Be(
            "read:space:confluence read:page:confluence read:comment:confluence read:attachment:confluence "
            + "read:folder:confluence read:content-details:confluence read:content.permission:confluence");
        AtlassianSiteScopes.Confluence.Select(s => s.Name).Should().Equal(AtlassianConnectionForm.ConfluenceScopeList.Split(' '));
    }

    [Fact]
    public void New_GivesEveryScopeAReason_MarksAttachmentsOptional_AndSizesCopyButtons()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        var rows = cut.FindAll("#atlassian-scopes-confluence li");
        rows.Should().HaveCount(AtlassianSiteScopes.Confluence.Count);
        foreach (var (row, scope) in rows.Zip(AtlassianSiteScopes.Confluence))
        {
            row.TextContent.Should().Contain(scope.Reason);
            row.TextContent.Contains("Optional").Should().Be(scope.Optional, scope.Name);
            var copy = row.QuerySelector("button")!;
            copy.GetAttribute("aria-label").Should().Be($"Copy {scope.Name}");
            copy.GetAttribute("style").Should().Contain("min-width:24px").And.Contain("min-height:24px");
        }
        AtlassianSiteScopes.Confluence.Where(s => s.Optional).Select(s => s.Name).Should().Equal("read:attachment:confluence");
    }

    [Fact]
    public void NewConfluenceSite_ShowsOnlyConfluenceScopesAndSteps_AsBeforeJira()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.FindAll("#atlassian-scopes-confluence code").Select(c => c.TextContent)
            .Should().Equal(AtlassianSiteScopes.Confluence.Select(s => s.Name));
        cut.FindAll("#atlassian-scopes-jira").Should().BeEmpty("a Confluence connection's credential holds Confluence scopes only");
        cut.FindAll("[id^='atlassian-copy-jira']").Should().BeEmpty();
        cut.Find("#atlassian-service-account-guide").TextContent.Should().NotContain("Jira");
        cut.Find("#atlassian-service-account-guide").TextContent.Should().Contain("view access").And.Contain("admin group");
        System.Text.RegularExpressions.Regex.Replace(cut.Find("#atlassian-site-intro").TextContent, @"\s+", " ").Trim().Should().Be(
            "A Confluence Cloud site, read as a service account. This service account reads your site's Confluence spaces for Confluence sources and checks each user's access. The site is saved once every check passes, or with Save anyway when the only problem is a warning.");
    }

    [Fact]
    public void NewJiraSite_ShowsOnlyJiraScopesAndItsOwnSteps()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Product, AtlassianProduct.Jira));

        var jira = cut.FindAll("#atlassian-scopes-jira li");
        jira.Select(li => li.QuerySelector("code")!.TextContent).Should().Equal(AtlassianSiteScopes.Jira.Select(s => s.Name));
        foreach (var (row, scope) in jira.Zip(AtlassianSiteScopes.Jira))
        {
            row.TextContent.Should().Contain(scope.Reason);
            row.TextContent.Contains("Optional").Should().Be(scope.Optional, scope.Name);
        }
        cut.FindAll("#atlassian-scopes-confluence").Should().BeEmpty("a Jira connection's credential holds Jira scopes only");

        var guide = cut.Find("#atlassian-service-account-guide").TextContent;
        guide.Should().Contain("Administer Jira").And.Contain("Browse Projects").And.Contain("Private").And.Contain("project role")
            .And.Contain("Jira Administration to App admin").And.Contain("jira-admins-").And.NotContain("admin role")
            .And.NotContain("Confluence");
        cut.Find("#atlassian-site-intro").TextContent.Should().Contain("Jira");
        cut.Markup.Should().Contain("The address in your browser when you open Jira");

        cut.Find("#atlassian-copy-required").Click();
        cut.Find("#atlassian-copy-with-attachments").Click();
        var copied = ctx.JSInterop.Invocations.Where(i => i.Identifier == "connapse.copyToClipboard")
            .Select(i => ((string)i.Arguments[0]!).Split(' ')).ToList();
        copied[0].Should().Equal(AtlassianSiteScopes.Jira.Where(s => !s.Optional).Select(s => s.Name));
        copied[1].Should().Equal(AtlassianSiteScopes.Jira.Select(s => s.Name));
        AtlassianConnectionForm.RequiredJiraScopeList.Split(' ').Should().Equal(copied[0]);
    }

    [Fact]
    public void NewJiraSite_CheckAndSave_TestsTheSiteForJiraOnly()
    {
        stub.AcceptCredentials = true;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Product, AtlassianProduct.Jira));

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-result"));
        stub.Requests.Should().Contain(r => r.StartsWith("/ex/jira/", StringComparison.Ordinal))
            .And.NotContain(r => r.StartsWith("/ex/confluence/", StringComparison.Ordinal));
    }

    [Fact]
    public void NewJiraSite_OnASiteSavedForConfluence_FillsInItsAddressAndSaysWhere()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p
            .Add(f => f.Product, AtlassianProduct.Jira)
            .Add(f => f.SuggestedSiteUrls, ["https://acme.atlassian.net"]));

        cut.Find("#atlassian-site-url").GetAttribute("value").Should().Be("https://acme.atlassian.net");
        cut.Find("#atlassian-site-url").GetAttribute("list").Should().Be("atlassian-known-sites");
        cut.Find("#atlassian-site-url-prefilled").TextContent.Should().Contain("this site's Confluence connection");

        // Back to Confluence, where that site is already saved: the address it was offered for Jira goes.
        cut.Render(p => p.Add(f => f.Product, AtlassianProduct.Confluence).Add(f => f.SuggestedSiteUrls, []));

        cut.Find("#atlassian-site-url").GetAttribute("value").Should().BeNullOrEmpty();
        cut.FindAll("#atlassian-site-url-prefilled").Should().BeEmpty();
    }

    [Fact]
    public void NewSite_ChoosingTheOtherProduct_DropsTheLastResult()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();
        FillAndSave(cut);
        cut.WaitForAssertion(() => cut.Find("#atlassian-save-result"));

        cut.Render(p => p.Add(f => f.Product, AtlassianProduct.Jira));

        cut.FindAll("#atlassian-save-result").Should().BeEmpty("that result was the Confluence test's");
        cut.Find("#atlassian-scopes-jira");
    }

    [Fact]
    public async Task CheckAndSave_ReportsWhileTheCheckRuns_SoThePageCanLockTheProduct()
    {
        stub.Hold();
        var reported = new List<bool>();
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.CheckingChanged, (bool checking) => reported.Add(checking)));

        FillAndSave(cut);
        await stub.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        reported.Should().Equal([true], "the check is running");

        stub.Release();
        cut.WaitForAssertion(() => reported.Should().Equal(true, false));
    }

    [Fact]
    public void CopyResult_ShowsBesideTheCopyButtons()
    {
        ctx.JSInterop.Setup<bool>("connapse.copyToClipboard", _ => true).SetResult(true);
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-copy-required").Click();

        cut.Find("#atlassian-copy-status").TextContent.Should().Be("Copied.");
        cut.Find("#atlassian-copy-status").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void ExistingJiraSite_ShowsOneStatusAndOnlyJiraScopes()
    {
        var site = AtlassianConnection(health: new ConnectionHealth(DateTime.UtcNow, Succeeded: true, Step: null, Message: "ok",
            Origin: "test", ConfirmedScopes: ["read:project:jira"])) with
        {
            ConfigJson = $$"""{"siteUrl":"https://acme.atlassian.net","cloudId":"{{CloudId}}","clientId":"client-1","product":"jira"}""",
        };

        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));

        cut.FindAll("#atlassian-site-products").Should().BeEmpty("a connection reads one product, so it has one status");
        cut.FindAll("#atlassian-site-details dt").Select(d => d.TextContent).Should().NotContain("Products");
        cut.Find("#atlassian-site-status").TextContent.Should().Contain("Working");
        cut.Find("#atlassian-site-scopes summary").TextContent.Should()
            .Be($"Permissions Connapse needs (1 of {AtlassianSiteScopes.Jira.Count} confirmed by the last test)");
        cut.FindAll("#atlassian-site-scopes code").Select(c => c.TextContent).Should().Equal(AtlassianSiteScopes.Jira.Select(s => s.Name));
    }

    [Fact]
    public void SetupGuideLink_RendersAsTheTitleRowLink_ToTheSiteConnectionSection()
    {
        var link = ctx.Render<Connapse.Web.Components.Shared.SetupGuideLink>(p => p
            .Add(l => l.Id, "atlassian-setup-guide")
            .Add(l => l.Href, AtlassianSiteFieldSpecs.SiteConnectionSection)).Find("#atlassian-setup-guide");

        link.TagName.Should().Be("A");
        link.GetAttribute("href").Should().Be("https://github.com/Destrayon/Connapse/blob/main/docs/sources/atlassian-setup.md#3-site-connection");
        link.GetAttribute("target").Should().Be("_blank");
        link.ClassList.Should().Contain("ms-auto", "it sits at the right of the title row");
        link.TextContent.Should().Contain("Atlassian setup guide").And.Contain("(opens in a new tab)");
    }

    [Fact]
    public void New_OnLocalhost_ShowsNoHttpWarning()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.FindAll("#atlassian-http-warning").Should().BeEmpty();
    }

    [Fact]
    public void New_OverPlainHttpOnTheNetwork_WarnsBeforeTheSecretIsEntered()
    {
        ctx.Services.AddSingleton<NavigationManager>(new LanNavigationManager());

        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-http-warning").TextContent.Should().Contain("plain HTTP").And.Contain("HTTPS");
    }

    [Fact]
    public void LookUpSite_AnnouncesTheCloudIdAndExplainsIt()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-resolve").TextContent.Should().Contain("Look up site");
        cut.Find("#atlassian-site-url").Input("acme.atlassian.net");
        cut.Find("#atlassian-resolve").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-cloud-id").TextContent.Should().Be(CloudId));
        var status = cut.Find("#atlassian-resolve-status");
        status.GetAttribute("role").Should().Be("status");
        status.TextContent.Should().Contain("Found the site").And.Contain("Atlassian's own ID for your site");
    }

    [Fact]
    public void Load_FlagsNoField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.FindAll(".is-invalid, [aria-invalid='true'], .invalid-feedback").Should().BeEmpty();
    }

    [Fact]
    public void Save_WithEmptyFields_ShowsAnErrorBesideEachField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-save").Click();

        foreach (string id in new[] { "atlassian-site-url", "atlassian-client-id", "atlassian-client-secret" })
        {
            cut.Find($"#{id}-error").TextContent.Should().NotBeNullOrWhiteSpace();
            var input = cut.Find($"#{id}");
            input.GetAttribute("aria-invalid").Should().Be("true");
            input.GetAttribute("aria-describedby").Should().Contain($"{id}-error");
        }
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public void SiteUrl_Malformed_IsFlaggedOnBlurBesideTheField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-site-url").Input("https://example.com");
        cut.FindAll("#atlassian-site-url-error").Should().BeEmpty("validation waits for blur or submit");
        cut.Find("#atlassian-site-url").FocusOut();

        cut.Find("#atlassian-site-url-error").TextContent.Should().Contain("https://your-site.atlassian.net");
        cut.Find("#atlassian-site-url").GetAttribute("aria-invalid").Should().Be("true");
    }

    [Fact]
    public void Save_WhenAProbeFails_ShowsThePlainStepLabelTroubleshootingAndDetails()
    {
        bool created = false;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.OnCreated, (Connection _) => created = true));

        FillAndSave(cut);

        cut.WaitForAssertion(() =>
            cut.Find("#atlassian-save-result").TextContent.Should().Contain("Atlassian rejected the client ID or secret."));
        var result = cut.Find("#atlassian-save-result");
        result.GetAttribute("role").Should().Be("alert");
        result.TextContent.Should().Contain("Signing in as the service account").And.NotContain("(step:").And.NotContain(Secret);
        result.QuerySelector("a.alert-link")!.GetAttribute("href")
            .Should().EndWith("docs/sources/atlassian-setup.md#client-id-or-secret-rejected");
        cut.Find("#atlassian-save-result-details").TextContent.Should().Contain("Details").And.Contain("HTTP 401");
        store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        created.Should().BeFalse();
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
        store.Received(1).RecordHealthAsync(site.Id, Arg.Is<ConnectionHealth>(h => h.Succeeded), Arg.Any<CancellationToken>());
        AssertSecretGone(cut);
        cut.FindAll("#atlassian-secret-kept").Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_WhileTheCheckRuns_StopsItAndSavesNothing(bool checkIgnoresCancellation)
    {
        stub.AcceptCredentials = true;
        stub.Hold(checkIgnoresCancellation);
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(AtlassianConnection());
        bool created = false, cancelled = false;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p
            .Add(f => f.OnCreated, (Connection _) => created = true)
            .Add(f => f.OnCancel, () => cancelled = true));

        FillAndSave(cut);
        await stub.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cut.Find("#atlassian-client-secret").HasAttribute("disabled").Should().BeTrue("the secret can't change mid-check");
        cut.Find("#atlassian-cancel").Click();
        stub.Release(); // a check that ignored the cancellation finishes, and still nothing is saved

        cut.WaitForAssertion(() => cut.Find("#atlassian-save").HasAttribute("disabled").Should().BeFalse());
        cancelled.Should().BeTrue();
        created.Should().BeFalse();
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);
        cut.FindAll("#atlassian-save-error").Should().BeEmpty("cancelling is not an error");
    }

    [Fact]
    public void Save_WhenAProbeFails_KeepsTheSecretForRetryButOutOfMarkupAndBoundFields()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-result").TextContent.Should().Contain("rejected"));
        AssertSecretGone(cut);
        cut.Find("#atlassian-secret-kept").TextContent.Should().Contain("Secret kept for your next attempt");

        cut.Find("button[aria-controls='atlassian-client-secret']").Click();
        AssertSecretGone(cut);
        cut.Find("#atlassian-client-secret").GetAttribute("value").Should().BeNullOrEmpty();

        // The retry needs no secret pasted again.
        stub.AcceptCredentials = true;
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(AtlassianConnection());
        cut.Find("#atlassian-save").Click();

        cut.WaitForAssertion(() => store.Received(1).CreateAsync(
            Arg.Is<CreateConnectionRequest>(r => r.Secret == Secret), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void ClearKeptSecret_DropsIt_SoTheNextSaveAsksForTheSecret()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();
        FillAndSave(cut);
        cut.WaitForAssertion(() => cut.Find("#atlassian-secret-kept"));

        cut.Find("#atlassian-secret-clear").Click();
        cut.FindAll("#atlassian-secret-kept").Should().BeEmpty();
        int requests = stub.Requests.Count;
        cut.Find("#atlassian-save").Click();

        cut.Find("#atlassian-client-secret-error").TextContent.Should().Contain("Paste the client secret");
        stub.Requests.Count.Should().Be(requests);
    }

    [Fact]
    public void Save_WhenTheSiteIsAlreadySaved_KeepsTheSecretOutOfStateAndMarkup()
    {
        stub.AcceptCredentials = true;
        // Another connection took the site's name between the check and the save.
        bool created = false;
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<Connection>(_ =>
            {
                created = true;
                throw new InvalidOperationException("exists");
            });
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => created
                ? [new Connection(Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.S3, "{}", null, DateTime.UtcNow, DateTime.UtcNow)]
                : []);
        var cut = ctx.Render<AtlassianConnectionForm>();

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-error").TextContent.Should().Contain("already named"));
        AssertSecretGone(cut);

        cut.Find("button[aria-controls='atlassian-client-secret']").Click();
        AssertSecretGone(cut);
        cut.Find("#atlassian-client-secret").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Save_SiteWithNoPages_WarnsWithoutBlocking_AndSaveAnywaySavesIt()
    {
        stub.AcceptCredentials = true;
        stub.NoPages = true;
        Connection site = AtlassianConnection();
        store.CreateAsync(Arg.Any<CreateConnectionRequest>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(site);
        Connection? created = null;
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.OnCreated, (Connection c) => created = c));

        FillAndSave(cut);

        cut.WaitForAssertion(() => cut.Find("#atlassian-save-result-save-anyway"));
        var result = cut.Find("#atlassian-save-result");
        result.ClassList.Should().Contain("alert-warning");
        result.GetAttribute("role").Should().Be("status");
        result.QuerySelector("a.alert-link")!.GetAttribute("href").Should().EndWith("#site-has-no-pages-or-no-other-users-yet");
        store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default);

        cut.Find("#atlassian-save-result-save-anyway").Click();

        cut.WaitForAssertion(() => created.Should().Be(site));
        store.Received(1).RecordHealthAsync(site.Id, Arg.Is<ConnectionHealth>(h => !h.Succeeded && h.Warning), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Save_PermissionCheckRefusedForAScope_LinksMissingScopeNotAdministrator()
    {
        stub.AcceptCredentials = true;
        stub.PermissionCheckScopeError = true;
        var cut = ctx.Render<AtlassianConnectionForm>();

        FillAndSave(cut);

        cut.WaitForAssertion(() =>
            cut.Find("#atlassian-save-result").TextContent.Should().Contain("read:content.permission:confluence"));
        var result = cut.Find("#atlassian-save-result");
        result.TextContent.Should().Contain("Checking access permissions").And.NotContain("isn't Confluence Administrator");
        result.QuerySelector("a.alert-link")!.GetAttribute("href").Should().EndWith("#missing-scope");
        cut.Find("#atlassian-save-result-details").TextContent.Should().Contain("HTTP 403").And.Contain("scope does not match");
    }

    private static void AssertSecretGone(IRenderedComponent<AtlassianConnectionForm> cut, string secret = Secret)
    {
        cut.Markup.Should().NotContain(secret);
        var fields = typeof(AtlassianConnectionForm).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        foreach (var field in fields.Where(f => f.FieldType == typeof(string)))
            ((string?)field.GetValue(cut.Instance)).Should().NotBe(secret, $"field {field.Name} must not hold the secret");
        foreach (var field in fields.Where(f => f.FieldType != typeof(string)))
            field.GetValue(cut.Instance)?.ToString().Should().NotBe(secret, $"field {field.Name} must not print the secret");
    }

    [Fact]
    public void Existing_ShowsTheSiteWithoutAnyEditableField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        cut.Find("#atlassian-site-details").TextContent.Should()
            .Contain("https://acme.atlassian.net").And.Contain(CloudId).And.Contain("client-1");
        cut.FindAll("#atlassian-site-url, #atlassian-client-id, #atlassian-client-secret, #atlassian-save").Should().BeEmpty();
        cut.FindAll("#atlassian-replace").Should().BeEmpty();
    }

    [Fact]
    public void Existing_TestConnectionIsSecondary_LeavingThePagesSaveNameTheOnlyFilledPrimary()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        cut.Find("#atlassian-retest").ClassList.Should().Contain("btn-outline-secondary").And.NotContain("btn-info");
        cut.FindAll(".btn-primary, .btn-info, .btn-success, .btn-danger").Should().BeEmpty(
            "Save name, on the page around this form, is the edit view's one primary action");
    }

    [Fact]
    public void Existing_SaysHowToRevokeTheCredentialInAtlassian_AndLinksTheGuide()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        var revoke = cut.Find("#atlassian-revoke-help");
        string text = System.Text.RegularExpressions.Regex.Replace(revoke.TextContent, @"\s+", " ");
        text.Should().Contain("Atlassian Administration").And.Contain("Directory → Service accounts")
            .And.Contain("delete its OAuth 2.0 credential");
        revoke.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().Equal(
            "https://admin.atlassian.com",
            "https://github.com/Destrayon/Connapse/blob/main/docs/sources/atlassian-setup.md#3-site-connection");
    }

    [Fact]
    public void Form_LeavesTheGuideLinkToTheConnectionsTitleRow()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.FindAll("#atlassian-setup-guide").Should().BeEmpty("the page's title row carries it, whichever provider is chosen");
    }

    [Fact]
    public void New_CredentialFields_SayWhatTheValuesLookLike()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Markup.Should().Contain("one string of letters and digits, with no spaces")
            .And.Contain("One long string of characters, shown only once, when the credential is created.");
    }

    [Fact]
    public void Existing_ShowsStatusLastSuccessAndWhichScopesTheLastTestConfirmed()
    {
        var checkedAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        var site = AtlassianConnection(health: new ConnectionHealth(checkedAt, Succeeded: true, Step: null, Message: "ok",
            Origin: "test", LastSucceededAt: checkedAt, ConfirmedScopes: ["read:space:confluence", "read:page:confluence"]));

        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));

        cut.Find("#atlassian-site-status").TextContent.Should().Contain("Working");
        cut.Find("#atlassian-site-last-success").TextContent.Should().Contain("2026-10-01 09:30 UTC");
        var scopes = cut.Find("#atlassian-site-scopes");
        scopes.QuerySelector("summary")!.TextContent.Should().Be("Permissions Connapse needs (2 of 7 confirmed by the last test)");
        cut.FindAll("#atlassian-site-products").Should().BeEmpty("a Confluence connection looks as it did before Jira");
        scopes.QuerySelectorAll("li").Select(li => li.TextContent.Contains("Not confirmed"))
            .Should().Equal(false, false, true, true, true, true, true);
    }

    [Fact]
    public void Existing_WhoseCredentialWasRefused_SaysItNeedsNewCredentialsAndOffersReplace()
    {
        var site = AtlassianConnection(health: new ConnectionHealth(DateTime.UtcNow, Succeeded: false, Step: "token",
            Message: "Atlassian refused the stored credential while syncing source 'x'.", Origin: "sync", CredentialRejected: true));

        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));

        var alert = cut.Find("#atlassian-needs-credentials");
        alert.TextContent.Should().Contain("Needs new credentials");
        cut.Find("#atlassian-site-status").TextContent.Should().Contain("Needs new credentials");
        alert.QuerySelector("button").Should().BeNull("Replace credentials renders once, below the details, and isn't styled as destructive");
        cut.FindAll("button").Where(b => b.TextContent.Contains("Replace credentials")).Should().ContainSingle()
            .Which.ClassList.Should().NotContain("btn-danger");
        cut.Find("#atlassian-replace-open").Click();
        cut.Find("#atlassian-replace-consequence").TextContent.Should().Contain("its 2 sources will then sync").And.Contain("nothing changes");
    }

    [Fact]
    public void TestAgain_RunsTheProbesWithTheStoredSecretAndShowsTheFailingStep()
    {
        Connection site = AtlassianConnection();
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);

        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));
        cut.Find("#atlassian-retest").Click();

        cut.WaitForAssertion(() =>
            cut.Find("#atlassian-retest-result").TextContent.Should()
                .Contain("Atlassian rejected the client ID or secret.").And.Contain("Signing in as the service account"));
        cut.Find("#atlassian-retest-result").TextContent.Should().NotContain("(step:");
        store.Received(1).GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>());
        store.Received(1).RecordHealthAtAsync(site.Id,
            Arg.Is<ConnectionHealth>(h => !h.Succeeded && h.CredentialRejected && h.Step == "token"), site.UpdatedAt, Arg.Any<CancellationToken>());
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void TestAgain_ShowsTheRecordedHealth_EvenWhenTheParentRendersItsStaleCopyAgain()
    {
        Connection site = AtlassianConnection();
        Connection refreshed = site with
        {
            Health = new ConnectionHealth(DateTime.UtcNow, Succeeded: false, Step: "token", Message: "m", Origin: "test", CredentialRejected: true),
        };
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        store.GetSecretAtAsync(site.Id, site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(Secret);
        store.RecordHealthAtAsync(site.Id, Arg.Any<ConnectionHealth>(), site.UpdatedAt, Arg.Any<CancellationToken>()).Returns(refreshed.Health);
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));

        cut.Find("#atlassian-retest").Click();
        cut.WaitForAssertion(() => cut.Find("#atlassian-site-status").TextContent.Should().Contain("Needs new credentials"));
        cut.Render(p => p.Add(f => f.Existing, site));

        cut.Find("#atlassian-site-status").TextContent.Should().Contain("Needs new credentials");
    }

    [Fact]
    public void ReplaceCredentials_WhenTheNewOnesPass_UpdatesTheConnectionInPlace()
    {
        stub.AcceptCredentials = true;
        Connection site = AtlassianConnection();
        Connection? updated = null;
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        store.UpdateAsync(site.Id, Arg.Any<UpdateConnectionRequest>(), Arg.Any<CancellationToken>()).Returns(site);
        var cut = ctx.Render<AtlassianConnectionForm>(p => p
            .Add(f => f.Existing, site)
            .Add(f => f.OnUpdated, (Connection c) => updated = c));

        cut.Find("#atlassian-replace-open").Click();
        cut.Find("#atlassian-replace-client-id").Input("client-2");
        cut.Find("#atlassian-replace-client-secret").Change(NewSecret);
        cut.Find("#atlassian-replace-save").Click();

        cut.WaitForAssertion(() => updated.Should().Be(site));
        store.Received(1).UpdateAsync(site.Id,
            Arg.Is<UpdateConnectionRequest>(r => r.Secret == NewSecret && r.Name == null && r.ConfigJson!.Contains("\"clientId\":\"client-2\"")
                                                 && r.ConfigJson.Contains(CloudId)),
            Arg.Any<CancellationToken>());
        store.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        audit.Received(1).LogAsync("connection.credentials_replaced", "connection", site.Id.ToString(), Arg.Any<object>(), Arg.Any<CancellationToken>());
        stub.TokenBodies.Should().Contain(b => b.Contains(NewSecret));
        cut.FindAll("#atlassian-replace").Should().BeEmpty();
        AssertSecretGone(cut, NewSecret);
    }

    [Fact]
    public void ReplaceCredentials_WhenTheNewOnesFail_ChangesNothingAndKeepsTheSecretForRetry()
    {
        Connection site = AtlassianConnection();
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));

        cut.Find("#atlassian-replace-open").Click();
        cut.Find("#atlassian-replace-client-id").Input("client-2");
        cut.Find("#atlassian-replace-client-secret").Change(NewSecret);
        cut.Find("#atlassian-replace-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-replace-result").TextContent.Should().Contain("Signing in as the service account"));
        store.DidNotReceiveWithAnyArgs().UpdateAsync(default, default!, default);
        cut.Find("#atlassian-replace-kept").TextContent.Should().Contain("Secret kept for your next attempt");
        AssertSecretGone(cut, NewSecret);
    }

    [Fact]
    public void ReplaceCredentials_MovesFocusToItsFirstField_AndCancelReturnsItToTheButton()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        cut.Find("#atlassian-replace-open").Click();
        cut.FindAll("#atlassian-replace-open").Should().BeEmpty();
        ctx.LastFocused(cut)!.Id.Should().Be("atlassian-replace-client-id");

        cut.FindAll("#atlassian-replace button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        ctx.LastFocused(cut)!.Id.Should().Be("atlassian-replace-open");
    }

    [Fact]
    public void ClearIt_OnTheKeptReplacementSecret_MovesFocusToTheSecretField()
    {
        Connection site = AtlassianConnection();
        store.GetAsync(site.Id, Arg.Any<CancellationToken>()).Returns(site);
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, site));
        cut.Find("#atlassian-replace-open").Click();
        cut.Find("#atlassian-replace-client-id").Input("client-2");
        cut.Find("#atlassian-replace-client-secret").Change(NewSecret);
        cut.Find("#atlassian-replace-save").Click();
        cut.WaitForAssertion(() => cut.Find("#atlassian-replace-kept"));

        cut.Find("#atlassian-replace-secret-clear").Click();

        cut.FindAll("#atlassian-replace-kept").Should().BeEmpty();
        ctx.LastFocusedRef().Should().NotBeNull().And.Be(SecretInputRef(cut, "atlassian-replace-client-secret"));
    }

    /// <summary>A secret field's input (CSS-isolated, so matched by its reference field).</summary>
    private static string? SecretInputRef(IRenderedComponent<AtlassianConnectionForm> cut, string id) =>
        FocusAssertions.RefField(cut.FindComponents<Connapse.Web.Components.Shared.SecretTextArea>().Single(s => s.Instance.Id == id).Instance, "input");

    /// <summary>
    /// The Connections page swaps its list and this form, taking the pressed button with them, so
    /// focus follows. The page needs too many services to render here, so its source is read, as
    /// ConnectionInUseListTests does for the dialog focus.
    /// </summary>
    [Fact]
    public void ConnectionsPage_MovesFocusWhenTheListAndTheFormReplaceEachOther()
    {
        string page = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Connections.razor"));

        page.Should().Contain("<h5 @ref=\"formHeading\" id=\"connection-form-title\" tabindex=\"-1\">");
        page.Should().Contain("@ref=\"addConnectionButton\"").And.Contain("@ref=\"editButtons[c.Id]\"");
        System.Text.RegularExpressions.Regex.Matches(page, @"focusAfterSwap = \(\) => formHeading\.FocusAsync\(\);").Count
            .Should().Be(2, "both Add connection and Edit open the form with focus on its heading");
        page.Should().Contain("ReturnFocusToList(isNew ? null : editing?.Id);")
            .And.Contain("ReturnFocusToList(created.Id);")
            .And.Contain("await FocusAfterSwapAsync();");
    }

    /// <summary>
    /// Every provider's edit on the Connections page is saved as the REST API renames a site, so a
    /// taken name is refused in plain words under the Name field and no exception text is shown.
    /// </summary>
    [Fact]
    public void ConnectionsPage_SavesEditsAsTheApiRenames_AndShowsNoRawExceptionText()
    {
        string page = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Connections.razor"));

        page.Should().Contain("await Operations.SaveAsync(await Authentication, editing!, form.Name,").And.NotContain("ConnectionStore.UpdateAsync(");
        page.Should().Contain("Fail, RefuseNameAsync, RefuseCustomFieldAsync);")
            .And.Contain("<div id=\"connection-name-error\" class=\"invalid-feedback d-block\" role=\"alert\">");
        page.Should().NotContain("Fail(ex.Message)", "the exception goes to the log and the page says what happened");
    }

    [Fact]
    public void CopyRequiredScopes_LeavesOutTheAttachmentPermission_WhichHasItsOwnCopy()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();

        cut.Find("#atlassian-copy-required").Click();
        cut.Find("#atlassian-copy-with-attachments").Click();

        var copied = ctx.JSInterop.Invocations.Where(i => i.Identifier == "connapse.copyToClipboard")
            .Select(i => (string)i.Arguments[0]!).ToList();
        copied.Should().Equal(AtlassianConnectionForm.RequiredScopeList, AtlassianConnectionForm.ConfluenceScopeList);
        copied[0].Split(' ').Should().HaveCount(6).And.NotContain("read:attachment:confluence");
        copied[1].Split(' ').Should().HaveCount(7).And.Contain("read:attachment:confluence");
        cut.Find("#atlassian-service-account-guide").TextContent.Should().Contain("Only if a source will include attachments");
    }

    [Fact]
    public void StartReplacing_OpensReplaceCredentialsWithFocusOnItsFirstField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p
            .Add(f => f.Existing, AtlassianConnection())
            .Add(f => f.StartReplacing, true));

        cut.Find("#atlassian-replace").Should().NotBeNull();
        cut.FindAll("#atlassian-replace-open").Should().BeEmpty();
        ctx.LastFocused(cut)!.Id.Should().Be("atlassian-replace-client-id");
    }

    [Fact]
    public void ConnectionsPage_OpensASiteWithReplaceCredentialsFromTheQuery()
    {
        string page = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Connections.razor"));

        page.Should().Contain("await OpenEditFromQueryAsync();")
            .And.Contain("query.TryGetValue(\"replace\", out var replace) && replace.ToString() == \"1\"")
            .And.Contain("StartReplacing=\"@atlassianStartReplacing\"");
        Connapse.Web.Components.Sources.ConfluenceSpaceSelection.ReplaceCredentialsUrl(Guid.Empty)
            .Should().Be($"/connections?edit={Guid.Empty}&replace=1");
    }

    [Fact]
    public void ClearIt_OnTheKeptSecret_MovesFocusToTheSecretField()
    {
        var cut = ctx.Render<AtlassianConnectionForm>();
        FillAndSave(cut);
        cut.WaitForAssertion(() => cut.Find("#atlassian-secret-kept"));

        cut.Find("#atlassian-secret-clear").Click();

        ctx.LastFocusedRef().Should().NotBeNull().And.Be(SecretInputRef(cut, "atlassian-client-secret"));
    }

    [Fact]
    public void ReplaceCredentials_WithEmptyFields_FlagsThemBesideTheFields()
    {
        var cut = ctx.Render<AtlassianConnectionForm>(p => p.Add(f => f.Existing, AtlassianConnection()));

        cut.Find("#atlassian-replace-open").Click();
        cut.Find("#atlassian-replace-save").Click();

        cut.Find("#atlassian-replace-client-id-error").TextContent.Should().Contain("client ID");
        cut.Find("#atlassian-replace-client-secret-error").TextContent.Should().Contain("client secret");
        stub.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ReplaceCredentials_TellsThePageWhenItOpensAndCloses_SoThePageKeepsOnePrimaryAction()
    {
        var changes = new List<bool>();
        var cut = ctx.Render<AtlassianConnectionForm>(p => p
            .Add(f => f.Existing, AtlassianConnection())
            .Add(f => f.ReplacingChanged, (bool open) => changes.Add(open)));

        cut.Find("#atlassian-replace-open").Click();
        cut.FindAll("#atlassian-replace .btn-primary").Should().ContainSingle()
            .Which.Id.Should().Be("atlassian-replace-save");
        cut.FindAll("button").Where(b => b.TextContent.Contains("Replace credentials")).Should().BeEmpty(
            "the open button is replaced by the section it opens");
        cut.FindAll("#atlassian-replace button").Single(b => b.TextContent == "Cancel").Click();

        changes.Should().Equal(true, false);
        cut.FindAll("#atlassian-replace-open").Should().ContainSingle();
    }

    [Fact]
    public void TestResult_ATimeoutOrRateLimit_LinksTheGeneralTroubleshootingSection_NotRejectedCredentials()
    {
        var result = ConnectionTestResult.CreateFailure(
            "Atlassian didn't answer in time. Wait a minute and test again.",
            new Dictionary<string, object> { ["step"] = "token", ["troubleshoot"] = AtlassianSiteTroubleshooting.General });

        var cut = ctx.Render<AtlassianTestResultView>(p => p.Add(v => v.Result, result).Add(v => v.Id, "r"));

        var link = cut.Find("#r a.alert-link");
        link.GetAttribute("href").Should().EndWith("atlassian-setup.md#troubleshooting");
        link.TextContent.Should().StartWith("Troubleshooting guide");
        cut.Markup.Should().NotContain("client-id-or-secret-rejected");
    }

    private sealed class LanNavigationManager : NavigationManager
    {
        public LanNavigationManager() => Initialize("http://connapse.lan/", "http://connapse.lan/connections");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class StubAtlassianHandler : HttpMessageHandler
    {
        /// <summary>When true the token endpoint and every Confluence probe succeed, as for a correct admin account.</summary>
        public bool AcceptCredentials { get; set; }

        /// <summary>The site has no pages, so administrator access can't be checked.</summary>
        public bool NoPages { get; set; }

        /// <summary>The permission check is refused because the credential lacks its scope.</summary>
        public bool PermissionCheckScopeError { get; set; }

        public List<string> Requests { get; } = [];
        public List<string> TokenBodies { get; } = [];

        private TaskCompletionSource? held;
        private bool holdIgnoresCancellation;

        /// <summary>The token request waits until <see cref="Release"/> or until the caller cancels.</summary>
        public void Hold(bool ignoreCancellation = false) =>
            (held, holdIgnoresCancellation) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), ignoreCancellation);

        public void Release() => held?.TrySetResult();

        /// <summary>Set once the held request is waiting, so a test can act while the check runs.</summary>
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add(path);
            if (held is not null && path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                Waiting.TrySetResult();
                await (holdIgnoresCancellation ? held.Task : held.Task.WaitAsync(ct));
            }
            string? json = null;

            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                lock (TokenBodies) TokenBodies.Add(Uri.UnescapeDataString(body));
            }

            if (path.EndsWith("/_edge/tenant_info", StringComparison.Ordinal))
                json = $$"""{"cloudId":"{{CloudId}}"}""";
            else if (AcceptCredentials && path.EndsWith("/oauth/token", StringComparison.Ordinal))
                json = """{"access_token":"t","expires_in":3600}""";
            else if (AcceptCredentials && path.EndsWith("/rest/api/user/current", StringComparison.Ordinal))
                json = """{"accountId":"service"}""";
            else if (AcceptCredentials && path.EndsWith("/api/v2/spaces", StringComparison.Ordinal))
                json = """{"results":[{"id":"1"}]}""";
            else if (AcceptCredentials && path.EndsWith("/api/v2/pages", StringComparison.Ordinal))
                json = NoPages ? """{"results":[]}""" : """{"results":[{"id":"10"}]}""";
            else if (AcceptCredentials && path.EndsWith("/rest/api/search/user", StringComparison.Ordinal))
                json = """{"results":[{"user":{"accountId":"someone-else"}}]}""";
            // The content step's probes: comments, folders, changes and people, and attachments.
            else if (AcceptCredentials && (path.EndsWith("/footer-comments", StringComparison.Ordinal)
                                           || path.EndsWith("/attachments", StringComparison.Ordinal)
                                           || path.EndsWith("/rest/api/search", StringComparison.Ordinal)
                                           || path.EndsWith("/rest/api/user/bulk", StringComparison.Ordinal)))
                json = """{"results":[]}""";
            else if (AcceptCredentials && path.Contains("/api/v2/folders/", StringComparison.Ordinal))
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            else if (AcceptCredentials && PermissionCheckScopeError && path.EndsWith("/permission/check", StringComparison.Ordinal))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"code":403,"message":"Unauthorized; scope does not match"}"""),
                };
            else if (AcceptCredentials && path.EndsWith("/permission/check", StringComparison.Ordinal))
                json = """{"hasPermission":true}""";

            return json is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
