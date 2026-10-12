using System.Net;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class AtlassianUserSignInTests
{
    private const string Redirect = "https://connapse.example/api/v1/auth/cloud/atlassian/callback";

    [Fact]
    public void AuthorizeUrl_WithoutPkce_IsTheReadMeConsentUrl()
    {
        AtlassianUserSignIn.AuthorizeUrl("client-1", Redirect, "state-1", codeChallenge: null).Should().Be(
            "https://auth.atlassian.com/authorize?audience=api.atlassian.com&client_id=client-1&scope=read%3Ame"
            + "&redirect_uri=https%3A%2F%2Fconnapse.example%2Fapi%2Fv1%2Fauth%2Fcloud%2Fatlassian%2Fcallback"
            + "&state=state-1&response_type=code&prompt=consent");
    }

    [Fact]
    public void AuthorizeUrl_WithPkce_AddsTheS256Challenge()
    {
        string url = AtlassianUserSignIn.AuthorizeUrl("client-1", Redirect, "state-1", "challenge-1");

        url.Should().EndWith("&code_challenge=challenge-1&code_challenge_method=S256");
        url.Should().NotContain("offline_access");
    }

    [Fact]
    public async Task ResolveAsync_MeWithoutAccountId_Throws()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { name = "Nobody" });

        var act = () => SignIn(api).ResolveAsync("code-1", "verifier-1", Redirect);

        await act.Should().ThrowAsync<AtlassianAuthException>();
    }

    [Fact]
    public async Task ResolveAsync_SendsTheVerifierAndReturnsOnlyTheAccountId()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { account_id = "acc-1", name = "Ada Lovelace", email = "ada@example.com" });

        var account = await SignIn(api).ResolveAsync("code-1", "verifier-1", Redirect);

        // /me also sends the name and email; neither is kept.
        account.Should().Be(new AtlassianUserAccount("acc-1"));
        api.AuthorizationCodeBodies.Should().ContainSingle()
            .Which.Should().Contain("\"code_verifier\":\"verifier-1\"").And.Contain("\"client_secret\":\"secret-1\"");
        api.Requests.Should().Contain(new Uri(AtlassianUserSignIn.MeEndpoint));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "invalid_grant", AtlassianLinkAppCheck.Accepted)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_grant", AtlassianLinkAppCheck.Accepted)]
    [InlineData(HttpStatusCode.Forbidden, "access_denied", AtlassianLinkAppCheck.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, "unauthorized_client", AtlassianLinkAppCheck.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, null, AtlassianLinkAppCheck.Rejected)]
    [InlineData(HttpStatusCode.InternalServerError, null, AtlassianLinkAppCheck.Unknown)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request", AtlassianLinkAppCheck.Unknown)]
    public void Classify_ReadsTheTokenEndpointsRefusal(HttpStatusCode status, string? error, AtlassianLinkAppCheck expected)
    {
        AtlassianUserSignIn.Classify(status, error).Should().Be(expected);
    }

    [Fact]
    public async Task CheckAppAsync_InvalidGrant_IsAcceptedAndSendsAMadeUpCode()
    {
        var token = new TokenEndpoint(HttpStatusCode.Forbidden, """{"error":"invalid_grant","error_description":"Invalid authorization code"}""");

        var result = await SignIn(token).CheckAppAsync(" client-9 ", "secret-9", Redirect);

        result.Should().Be(AtlassianLinkAppCheck.Accepted);
        token.Body.Should().Contain("\"grant_type\":\"authorization_code\"")
            .And.Contain("\"client_id\":\"client-9\"")
            .And.Contain("\"client_secret\":\"secret-9\"")
            .And.Contain($"\"code\":\"{AtlassianUserSignIn.CheckCode}\"");
    }

    // The check sends a code Atlassian never issued, so a 2xx is not Atlassian accepting the client:
    // it is a proxy, a captive portal or something else answering in its place.
    [Theory]
    [InlineData("<html><body>Sign in to the guest network</body></html>", "text/html")]
    [InlineData("", "application/json")]
    [InlineData("""{"access_token":"tok-1","token_type":"Bearer"}""", "application/json")]
    public async Task CheckAppWithDetailAsync_AnyTwoHundred_IsUnknown(string body, string contentType)
    {
        var token = new TokenEndpoint(HttpStatusCode.OK, body, contentType: contentType);

        var result = await SignIn(token).CheckAppWithDetailAsync("client-9", "secret-9", Redirect);

        result.Result.Should().Be(AtlassianLinkAppCheck.Unknown);
        result.Detail.Should().Be("HTTP 200 from the token endpoint, which should refuse a made-up code; check for a proxy");
    }

    [Fact]
    public async Task CheckStoredAppAsync_TwoHundred_RecordsNothing()
    {
        var credentials = Credentials();

        (await SignIn(new TokenEndpoint(HttpStatusCode.OK, """{"access_token":"tok-1"}"""), credentials).CheckStoredAppAsync(Redirect))
            .Should().Be(AtlassianLinkAppCheck.Unknown);

        await credentials.DidNotReceiveWithAnyArgs().RecordAtlassianLinkAppCheckAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task CheckAppAsync_AccessDenied_IsRejected()
    {
        var token = new TokenEndpoint(HttpStatusCode.Forbidden, """{"error":"access_denied","error_description":"Unauthorized"}""");

        (await SignIn(token).CheckAppAsync("client-9", "wrong", Redirect)).Should().Be(AtlassianLinkAppCheck.Rejected);
    }

    [Fact]
    public async Task CheckAppAsync_Unreachable_IsUnknown()
    {
        var token = new TokenEndpoint(throws: true);

        (await SignIn(token).CheckAppAsync("client-9", "secret-9", Redirect)).Should().Be(AtlassianLinkAppCheck.Unknown);
    }

    [Fact]
    public async Task CheckStoredAppAsync_RecordsTheAnswer()
    {
        var token = new TokenEndpoint(HttpStatusCode.Forbidden, """{"error":"access_denied"}""");
        var credentials = Credentials();

        var result = await SignIn(token, credentials).CheckStoredAppAsync(Redirect);

        result.Should().Be(AtlassianLinkAppCheck.Rejected);
        await credentials.Received(1).RecordAtlassianLinkAppCheckAsync("client-1", "secret-1", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckStoredAppAsync_Unreachable_RecordsNothing()
    {
        var credentials = Credentials();

        (await SignIn(new TokenEndpoint(throws: true), credentials).CheckStoredAppAsync(Redirect)).Should().Be(AtlassianLinkAppCheck.Unknown);

        await credentials.DidNotReceiveWithAnyArgs().RecordAtlassianLinkAppCheckAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SignInUrlAsync_SignInTurnedOff_Throws()
    {
        var credentials = Credentials(new AtlassianLinkAppRegistration("client-1", SignInEnabled: false));

        var act = () => SignIn(new TokenEndpoint(throws: true), credentials).SignInUrlAsync(Redirect, "state-1", null);

        await act.Should().ThrowAsync<AtlassianSignInOffException>();
    }

    [Fact]
    public async Task ResolveAsync_SignInTurnedOff_ThrowsWithoutCallingAtlassian()
    {
        var token = new TokenEndpoint(throws: true);
        var credentials = Credentials(new AtlassianLinkAppRegistration("client-1", SignInEnabled: false));

        var act = () => SignIn(token, credentials).ResolveAsync("code-1", null, Redirect);

        await act.Should().ThrowAsync<AtlassianSignInOffException>();
        token.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ResolveAsync_AppRejected_RecordsAFailedCheckAndThrows()
    {
        var token = new TokenEndpoint(HttpStatusCode.Forbidden, """{"error":"access_denied","error_description":"Unauthorized"}""");
        var credentials = Credentials();

        var act = () => SignIn(token, credentials).ResolveAsync("code-1", null, Redirect);

        await act.Should().ThrowAsync<AtlassianLinkAppRejectedException>();
        await credentials.Received(1).RecordAtlassianLinkAppCheckAsync("client-1", "secret-1", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAppWithDetailAsync_Rejected_KeepsAtlassiansErrorCode()
    {
        var token = new TokenEndpoint(HttpStatusCode.Forbidden, """{"error":"access_denied","error_description":"Unauthorized"}""");

        var result = await SignIn(token).CheckAppWithDetailAsync("client-9", "wrong", Redirect);

        result.Result.Should().Be(AtlassianLinkAppCheck.Rejected);
        result.Detail.Should().Be("HTTP 403, access_denied: Unauthorized");
        result.Detail.Should().NotContain("wrong", "the secret is never echoed");
    }

    [Fact]
    public async Task CheckAppWithDetailAsync_Accepted_HasNoDetail()
    {
        var token = new TokenEndpoint(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        (await SignIn(token).CheckAppWithDetailAsync("client-9", "secret-9", Redirect))
            .Should().Be(new AtlassianLinkAppCheckResult(AtlassianLinkAppCheck.Accepted));
    }

    [Fact]
    public async Task CheckAppWithDetailAsync_Unreachable_SaysWhatFailed()
    {
        var result = await SignIn(new TokenEndpoint(throws: true)).CheckAppWithDetailAsync("client-9", "secret-9", Redirect);

        result.Result.Should().Be(AtlassianLinkAppCheck.Unknown);
        result.Detail.Should().Be("No answer from auth.atlassian.com: No route to host");
    }

    [Fact]
    public async Task CheckStoredAppWithDetailAsync_RecordsTheAnswerAndKeepsTheCode()
    {
        var credentials = Credentials();

        var result = await SignIn(new TokenEndpoint(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""), credentials)
            .CheckStoredAppWithDetailAsync(Redirect);

        result!.Detail.Should().Be("HTTP 401, invalid_client");
        await credentials.Received(1).RecordAtlassianLinkAppCheckAsync("client-1", "secret-1", false, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_Completed_RemembersTheCallbackUrlForTheDailyCheck()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { account_id = "acc-1" });
        var credentials = Credentials();

        await SignIn(api, credentials).ResolveAsync("code-1", "verifier-1", Redirect);

        await credentials.Received(1).RememberAtlassianCallbackUrlAsync("client-1", Redirect, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_RememberingTheCallbackFails_StillLinks()
    {
        using var api = new FakeAtlassianApi();
        api.AcceptAuthorizationCode("code-1");
        api.MapJson("/me", new { account_id = "acc-1" });
        var credentials = Credentials();
        credentials.RememberAtlassianCallbackUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("db down"));

        (await SignIn(api, credentials).ResolveAsync("code-1", "verifier-1", Redirect)).AccountId.Should().Be("acc-1");
    }

    private static IProviderCredentialStore Credentials(AtlassianLinkAppRegistration? app = null)
    {
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAtlassianLinkAppAsync(Arg.Any<CancellationToken>()).Returns(app ?? new AtlassianLinkAppRegistration("client-1"));
        credentials.GetAtlassianLinkAppSecretAsync(Arg.Any<CancellationToken>()).Returns("secret-1");
        return credentials;
    }

    private static AtlassianUserSignIn SignIn(HttpMessageHandler handler, IProviderCredentialStore? credentials = null)
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(AtlassianApiClient.HttpClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new AtlassianUserSignIn(http, credentials ?? Credentials());
    }

    private static AtlassianUserSignIn SignIn(FakeAtlassianApi api, IProviderCredentialStore? credentials = null) =>
        SignIn((HttpMessageHandler)api, credentials);

    /// <summary>Atlassian's token endpoint answering every request the same way, or unreachable.</summary>
    private sealed class TokenEndpoint(
        HttpStatusCode status = HttpStatusCode.OK, string? json = null, bool throws = false, string contentType = "application/json") : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (throws)
                throw new HttpRequestException("No route to host");
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json ?? "", System.Text.Encoding.UTF8, contentType),
            };
        }
    }
}
