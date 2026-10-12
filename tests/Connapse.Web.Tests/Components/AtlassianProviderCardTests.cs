using System.Net;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Providers;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class AtlassianProviderCardTests : IDisposable
{
    private const string Secret = "super-secret-value";
    private const string InvalidGrant = """{"error":"invalid_grant","error_description":"Invalid authorization code"}""";
    private const string AccessDenied = """{"error":"access_denied","error_description":"Unauthorized"}""";

    private readonly BunitContext ctx = new();
    private readonly IProviderCredentialStore credentials = Substitute.For<IProviderCredentialStore>();
    private readonly IAtlassianIdentityLinkReader links = Substitute.For<IAtlassianIdentityLinkReader>();
    private readonly IAuditLogger audit = Substitute.For<IAuditLogger>();
    private readonly TokenEndpoint token = new();

    public AtlassianProviderCardTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization();
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton(credentials);
        ctx.Services.AddSingleton(links);
        ctx.Services.AddSingleton(audit);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(AtlassianApiClient.HttpClientName).Returns(_ => new HttpClient(token, disposeHandler: false));
        ctx.Services.AddSingleton(new AtlassianUserSignIn(http, credentials));
        ctx.Services.AddScoped<AtlassianLinkAppService>();
        token.Answer(HttpStatusCode.Forbidden, InvalidGrant);
    }

    public void Dispose()
    {
        ctx.Dispose();
        token.Dispose();
    }

    [Fact]
    public void Render_ShowsOnlyTheLinkingApp_AndSendsSitesToTheConnectionsPage()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("section.provider-step h2").Select(h => h.TextContent)
            .Should().Equal("Linking app");
        cut.FindAll("#atlassian-site-url").Should().BeEmpty();
        cut.Markup.Should().NotContain("read:page:confluence");
        cut.Find("#atlassian-sites-elsewhere a").GetAttribute("href").Should().Be("/connections?new=atlassian");
    }

    [Fact]
    public void LinkingApp_NotSetUp_ShowsTheConsoleLinkCallbackUrlAndScope()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        var step = cut.Find("#atlassian-app");
        step.TextContent.Should().Contain("Not set up")
            .And.Contain("User identity API").And.Contain("read:me")
            .And.Contain("Distribution").And.Contain("Sharing");
        step.InnerHtml.Should().Contain("https://developer.atlassian.com/console/myapps/");
        cut.Find("#atlassian-callback-url").TextContent.Should().Be("http://localhost/api/v1/auth/cloud/atlassian/callback");
        cut.FindAll("#atlassian-copy-callback").Should().ContainSingle();
    }

    [Fact]
    public void LinkingApp_NotSetUp_SaysWhatIsMissingAndTheHttpsRule()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-requirement").TextContent.Should().Be(ProviderSetupReader.AtlassianNoLinkAppDetail);
        cut.Find("#atlassian-app-prerequisites").TextContent.Should().Contain("HTTPS").And.Contain("localhost");
        cut.FindAll("#atlassian-app-insecure").Should().BeEmpty("localhost is exempt");
    }

    [Fact]
    public void LinkingApp_GuideRendersAboveTheFieldsItExplains_UnderAHeadingItNames()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        string html = cut.Find("#atlassian-app").InnerHtml;
        html.IndexOf("developer.atlassian.com/console", StringComparison.Ordinal)
            .Should().BeLessThan(html.IndexOf("id=\"atlassian-app-client-id\"", StringComparison.Ordinal));
        // GitHub's heading for hand-entered values, so the two provider pages read alike.
        cut.Find("#atlassian-app-credentials-heading").TextContent.Should().Be("Manual values");
        cut.Find("#atlassian-app").TextContent.Should().Contain("paste them under Manual values below")
            .And.NotContain("App credentials")
            .And.Contain("Atlassian's name for signing people in");
    }

    [Fact]
    public void LinkingApp_Stored_SummaryOffersAddASite_AsGitHubsOffersAddAConnection()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(new AtlassianLinkAppRegistration("stored-client", CheckedAt: DateTime.UtcNow, CheckAccepted: true));

        var cut = ctx.Render<AtlassianProviderCard>();

        var add = cut.Find("#atlassian-app-summary #atlassian-add-site");
        add.GetAttribute("href").Should().Be("/connections?new=atlassian");
        add.ClassList.Should().Contain(["btn", "btn-sm", "btn-outline-primary"]);
        add.TextContent.Trim().Should().Be("Add an Atlassian site");
    }

    [Fact]
    public void LinkingApp_NotStored_SummaryHasNoAddASiteButton()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("#atlassian-add-site").Should().BeEmpty();
    }

    [Fact]
    public void CheckAndSave_UsesGitHubsOutlineStyle()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-save").ClassList.Should().Contain("btn-outline-primary").And.NotContain("btn-primary");
    }

    [Fact]
    public void Fields_SayWhatTheValuesLookLike_BeforeAnythingIsSubmitted()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app").TextContent.Should().Contain("one string of letters and digits, with no spaces")
            .And.Contain("Labelled Secret, below the client ID: one long string of characters.");
    }

    [Fact]
    public void CopyButton_HasANameAndATargetOfAtLeast24Pixels()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        var copy = cut.Find("#atlassian-copy-callback");
        copy.GetAttribute("aria-label").Should().Be("Copy the callback URL");
        copy.GetAttribute("style").Should().Contain("min-width:24px").And.Contain("min-height:24px");
    }

    [Fact]
    public void Save_EmptyFields_MarksEachFieldInvalidWithItsOwnError()
    {
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-save").Click();

        var id = cut.Find("#atlassian-app-client-id");
        id.GetAttribute("aria-invalid").Should().Be("true");
        id.GetAttribute("aria-describedby").Should().Contain("atlassian-app-client-id-error").And.Contain("atlassian-app-client-id-help");
        cut.Find("#atlassian-app-client-id-error").TextContent.Should().Contain("Enter the client ID");
        cut.Find("#atlassian-app-client-secret-error").TextContent.Should().Contain("Enter the client secret");
        token.Calls.Should().Be(0);
        credentials.DidNotReceiveWithAnyArgs().SaveAtlassianLinkAppAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void LinkingApp_Save_ChecksWithAtlassianThenStoresTheAppAndNeverRendersTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client", CheckedAt: DateTime.UtcNow, CheckAccepted: true));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app").TextContent.Should().Contain("Ready"));
        token.Body.Should().Contain("\"client_id\":\"link-client\"").And.Contain(AtlassianUserSignIn.CheckCode);
        credentials.Received(1).SaveAtlassianLinkAppAsync("link-client", Secret, Arg.Any<Guid?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app-summary").TextContent.Should().Contain("link-client").And.Contain("accepted by Atlassian");
        cut.Find("#atlassian-app-status").TextContent.Should().Contain("Atlassian accepted");
        cut.Find("#atlassian-app-status a[href='/profile/integrations']").Should().NotBeNull();
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Save_Rejected_SavesNothingAndKeepsTheSecretToCorrect()
    {
        token.Answer(HttpStatusCode.Forbidden, AccessDenied);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-client-secret-error").TextContent.Should().Contain("Atlassian rejected"));
        cut.Find("#atlassian-app-client-secret").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("#atlassian-app-client-secret-error a").GetAttribute("href").Should().EndWith("#linking-app-rejected");
        cut.Find("#atlassian-app-client-secret").GetAttribute("value").Should().Be(Secret);
        credentials.DidNotReceiveWithAnyArgs().SaveAtlassianLinkAppAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void LinkingApp_Save_AtlassianUnreachable_SavesNothingAndSaysSo()
    {
        token.Unreachable();
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-error").TextContent.Should().Contain("Couldn't reach Atlassian"));
        cut.Find("#atlassian-app-error").GetAttribute("role").Should().Be("alert");
        credentials.DidNotReceiveWithAnyArgs().SaveAtlassianLinkAppAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void LinkingApp_Saved_IsReadyAndShowsTheClientIdButNotTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(new AtlassianLinkAppRegistration("stored-client"));
        links.CountLinksAsync(Arg.Any<CancellationToken>()).Returns(3);

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app").TextContent.Should().Contain("Ready");
        var summary = cut.Find("#atlassian-app-summary").TextContent;
        summary.Should().Contain("stored-client").And.Contain("read:me").And.Contain("Not yet");
        cut.Find("#atlassian-app-linked").TextContent.Should().Be("3");
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void LinkingApp_Save_WritesTheCredentialSavedAuditWithoutTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client"));
        object? details = null;
        audit.When(a => a.LogAsync("provider.credential.saved", "provider", "atlassian", Arg.Any<object?>(), Arg.Any<CancellationToken>()))
            .Do(call => details = call.ArgAt<object?>(3));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => details.Should().NotBeNull());
        System.Text.Json.JsonSerializer.Serialize(details).Should().Contain("link-client").And.NotContain(Secret);
        audit.ReceivedCalls().Select(c => System.Text.Json.JsonSerializer.Serialize(c.GetArguments()[3]))
            .Should().NotContain(json => json.Contains(Secret), "the check is audited too, without the secret");
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
    public void Replace_AsksFirstAndSaysWhatHappensToLinkedPeople_ThenChecksAndSaves()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("old-client"));
        links.CountLinksAsync(Arg.Any<CancellationToken>()).Returns(2);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("button[aria-controls='atlassian-app-setup']").Click();
        cut.Find("#atlassian-app-client-id").Input("new-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.Find("#atlassian-app-replace").TextContent.Should().Contain("2 people").And.Contain("keep their links").And.Contain("New sign-ins use the new app");
        token.Calls.Should().Be(0, "nothing is checked or saved until the replacement is confirmed");

        cut.Find("#atlassian-app-replace-confirm").Click();

        cut.WaitForAssertion(() => credentials.Received(1).SaveAtlassianLinkAppAsync("new-client", Secret, Arg.Any<Guid?>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Cancel_RestoresTheStoredClientIdAndClearsTheSecret()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("old-client"));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("button[aria-controls='atlassian-app-setup']").Click();
        cut.Find("#atlassian-app-client-id").Input("typo");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-cancel").Click();

        cut.FindAll("#atlassian-app-client-id").Should().BeEmpty("cancelling closes the edit view");
        cut.Find("button[aria-controls='atlassian-app-setup']").Click();
        cut.Find("#atlassian-app-client-id").GetAttribute("value").Should().Be("old-client");
        cut.Find("#atlassian-app-client-secret").GetAttribute("value").Should().BeNullOrEmpty();
        credentials.DidNotReceiveWithAnyArgs().SaveAtlassianLinkAppAsync(default!, default!, default, default, default);
    }

    [Fact]
    public void Recheck_Rejected_ShowsFailedAndAnnouncesIt()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(
            new AtlassianLinkAppRegistration("link-client"),
            new AtlassianLinkAppRegistration("link-client", CheckedAt: DateTime.UtcNow, CheckAccepted: false));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns(Secret);
        token.Answer(HttpStatusCode.Forbidden, AccessDenied);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-recheck").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-alert").TextContent.Should().Contain("Atlassian rejected the stored client ID or secret"));
        credentials.Received(1).RecordAtlassianLinkAppCheckAsync("link-client", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app").ClassList.Should().Contain("provider-step--failed");
        cut.Find("#atlassian-app-checked").TextContent.Should().Contain("rejected by Atlassian");
        cut.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void Recheck_Accepted_AnnouncesItInAStatusRegion()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("link-client"));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns(Secret);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-recheck").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-status").TextContent.Should().Contain("Atlassian accepted"));
        cut.Find("#atlassian-app-status").GetAttribute("role").Should().Be("status");
        credentials.Received(1).RecordAtlassianLinkAppCheckAsync("link-client", true, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TurnOffSignIn_IsSeparateFromRemove_AndIsAudited()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(
            new AtlassianLinkAppRegistration("link-client"),
            new AtlassianLinkAppRegistration("link-client", SignInEnabled: false));
        credentials.SetAtlassianSignInEnabledAsync(false, Arg.Any<CancellationToken>()).Returns(true);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-sign-in-toggle").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-status").TextContent.Should().Contain("Atlassian sign-in is off"));
        credentials.Received(1).SetAtlassianSignInEnabledAsync(false, Arg.Any<CancellationToken>());
        credentials.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        audit.Received(1).LogAsync("provider.sign_in.disabled", "provider", "atlassian", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app").ClassList.Should().Contain("provider-step--warning");
        cut.Find("#atlassian-sign-in-toggle").TextContent.Should().Contain("Turn on Atlassian sign-in");
    }

    [Fact]
    public void Remove_NeedsTheClientIdTyped_SaysLinksStay_DeletesAndAudits()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(
            new AtlassianLinkAppRegistration("link-client"), new AtlassianLinkAppRegistration("link-client"), (AtlassianLinkAppRegistration?)null);
        links.CountLinksAsync(Arg.Any<CancellationToken>()).Returns(4);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove the linking app from Connapse")).Click();
        cut.Markup.Should().Contain("4 people linked an Atlassian account. They keep their links, but nobody can link a new account");
        var confirm = cut.FindAll("button").Single(b => b.TextContent == "Remove the linking app");
        confirm.HasAttribute("disabled").Should().BeTrue();
        cut.Find("input[id^='reset-confirm-']").Input("link-client");
        cut.FindAll("button").Single(b => b.TextContent == "Remove the linking app").Click();

        cut.WaitForAssertion(() => credentials.Received(1).DeleteAsync("atlassian", Arg.Any<CancellationToken>()));
        audit.Received(1).LogAsync("provider.credential.deleted", "provider", "atlassian", Arg.Any<object?>(), Arg.Any<CancellationToken>());
        cut.Find("#atlassian-app-status").TextContent.Should().Contain("nobody can link an account until a new app is added");
    }

    [Fact]
    public void Save_Rejected_KeepsAtlassiansErrorCodeUnderDetails()
    {
        token.Answer(HttpStatusCode.Forbidden, AccessDenied);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-secret-detail").TextContent.Should().Contain("HTTP 403").And.Contain("access_denied"));
        cut.Find("#atlassian-app-secret-detail summary").TextContent.Should().Be("Details");
        cut.Find("#atlassian-app-client-secret-error").TextContent.Should().StartWith("Atlassian rejected this client ID and secret",
            "the plain message comes first; the code is there on demand");
    }

    [Fact]
    public void Save_Unreachable_LinksTheTroubleshootingEntryAndShowsWhatFailed()
    {
        token.Unreachable();
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-error a").GetAttribute("href").Should().EndWith("atlassian-setup.md#couldnt-reach-atlassian"));
        cut.Find("#atlassian-app-error-detail").TextContent.Should().Contain("No route to host");
    }

    [Fact]
    public void Recheck_Rejected_ShowsAtlassiansAnswerUnderDetails()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("link-client"));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns(Secret);
        token.Answer(HttpStatusCode.Forbidden, AccessDenied);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-recheck").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-alert-detail").TextContent.Should().Contain("HTTP 403, access_denied"));
        cut.Find("#atlassian-app-alert a").GetAttribute("href").Should().EndWith("#linking-app-rejected");
    }

    [Fact]
    public void Recheck_Unreachable_LinksTheTroubleshootingEntry()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration("link-client"));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns(Secret);
        token.Unreachable();
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-recheck").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-alert").TextContent.Should().Contain("Couldn't reach Atlassian"));
        cut.Find("#atlassian-app-alert a").GetAttribute("href").Should().EndWith("#couldnt-reach-atlassian");
    }

    [Fact]
    public void Save_RemembersTheCallbackUrlForTheDailyCheck()
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>())
            .Returns(null, new AtlassianLinkAppRegistration("link-client", CheckedAt: DateTime.UtcNow, CheckAccepted: true));
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => credentials.Received(1).RememberAtlassianCallbackUrlAsync(
            "link-client", "http://localhost/api/v1/auth/cloud/atlassian/callback", Arg.Any<CancellationToken>()));
    }

    [Theory]
    [InlineData("http://localhost/api/v1/auth/cloud/atlassian/callback", true, "Checked automatically every day.")]
    [InlineData(null, true, "once Recheck has run here")]
    [InlineData("http://localhost/api/v1/auth/cloud/atlassian/callback", false, "Not checked automatically while sign-in is off.")]
    public void LastChecked_SaysWhenTheAppIsCheckedAgain(string? callbackUrl, bool signInEnabled, string expected)
    {
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(new AtlassianLinkAppRegistration(
            "link-client", CheckedAt: DateTime.UtcNow, CheckAccepted: true, SignInEnabled: signInEnabled, CallbackUrl: callbackUrl));

        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-check-schedule").TextContent.Should().Contain(expected);
    }

    [Fact]
    public void TurnOffSignIn_IsBusyUntilDone_ThenAnnouncesTheResult()
    {
        var pending = new TaskCompletionSource<bool>();
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(
            new AtlassianLinkAppRegistration("link-client"),
            new AtlassianLinkAppRegistration("link-client", SignInEnabled: false));
        credentials.SetAtlassianSignInEnabledAsync(false, Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-sign-in-toggle").Click();

        var toggle = cut.Find("#atlassian-sign-in-toggle");
        toggle.HasAttribute("disabled").Should().BeTrue("a second click must not send a second change");
        toggle.TextContent.Should().Contain("Turning off");
        toggle.QuerySelector(".spinner-border").Should().NotBeNull();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove the linking app from Connapse"))
            .HasAttribute("disabled").Should().BeTrue("removing while sign-in is changing would race it");

        pending.SetResult(true);

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-status").TextContent.Should().Contain("Atlassian sign-in is off"));
        cut.Find("#atlassian-sign-in-toggle").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Remove_IsBusyUntilDone_ThenAnnouncesTheResult()
    {
        var pending = new TaskCompletionSource<bool>();
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(
            new AtlassianLinkAppRegistration("link-client"), new AtlassianLinkAppRegistration("link-client"), (AtlassianLinkAppRegistration?)null);
        credentials.DeleteAsync("atlassian", Arg.Any<CancellationToken>()).Returns(pending.Task);
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Remove the linking app from Connapse")).Click();
        cut.Find("input[id^='reset-confirm-']").Input("link-client");
        cut.FindAll("button").Single(b => b.TextContent == "Remove the linking app").Click();

        var busy = cut.FindAll("button").Single(b => b.TextContent.Contains("Removing"));
        busy.HasAttribute("disabled").Should().BeTrue();
        busy.QuerySelector(".spinner-border").Should().NotBeNull();
        cut.Find("#atlassian-sign-in-toggle").HasAttribute("disabled").Should().BeTrue();

        pending.SetResult(true);

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-status").TextContent.Should().Contain("The linking app was removed"));
    }

    [Fact]
    public void Save_DisablesBothFieldsWhileAtlassianIsAsked()
    {
        token.Hold();
        var cut = ctx.Render<AtlassianProviderCard>();

        cut.Find("#atlassian-app-client-id").Input("link-client");
        cut.Find("#atlassian-app-client-secret").Change(Secret);
        cut.Find("#atlassian-app-save").Click();

        cut.WaitForAssertion(() => cut.Find("#atlassian-app-client-secret").HasAttribute("disabled").Should().BeTrue());
        cut.Find("#atlassian-app-client-id").HasAttribute("disabled").Should().BeTrue();

        token.Release();
        cut.WaitForAssertion(() => cut.Find("#atlassian-app-client-secret").HasAttribute("disabled").Should().BeFalse());
    }

    /// <summary>Atlassian's token endpoint, answering every request one way, or unreachable.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private HttpStatusCode status;
        private string json = "";
        private bool unreachable;
        private TaskCompletionSource? held;

        public string? Body { get; private set; }
        public int Calls { get; private set; }

        public void Answer(HttpStatusCode answerStatus, string answerJson) => (status, json, unreachable) = (answerStatus, answerJson, false);

        public void Unreachable() => unreachable = true;

        /// <summary>Requests wait until <see cref="Release"/>, so a test can see what the page does meanwhile.</summary>
        public void Hold() => held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => held?.SetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (held is not null)
                await held.Task;
            if (unreachable)
                throw new HttpRequestException("No route to host");
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
