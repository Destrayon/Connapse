using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Where the AWS identity link is actually saved, and what it refuses to save.
/// </summary>
/// <remarks>
/// The assertion consumer cannot do this. It is reached by a cross-site POST from AWS carrying no
/// session, so it can only learn who started the sign-in from a nonce — and a nonce does not prove
/// the browser completing the sign-in is the browser that began it. Anybody with an account can
/// start one and send the Identity Center URL to a colleague, whose genuine assertion then comes
/// back attached to the starter's account.
/// <para>
/// These tests are the proof that the second step closes it. They drive the real endpoint with the
/// real singleton store rather than a stand-in, because what is being tested is the pairing of a
/// cookie with a session, and both live in the pipeline.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SamlLinkConfirmationTests(SharedWebAppFixture fixture)
{
    private const string ConfirmUrl = "/api/v1/auth/cloud/aws/confirm";
    private const string CookieName = "__connapse_aws_link";

    /// <summary>A client that reports redirects rather than following them.</summary>
    /// <remarks>
    /// The outcome of this endpoint <i>is</i> the redirect target — following it would replace the
    /// answer with the integrations page for every case, refused and accepted alike.
    /// </remarks>
    private HttpClient AdminClientNoRedirect()
    {
        var client = fixture.Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        return client;
    }

    /// <summary>The admin's own user id, read from the token the fixture signed in with.</summary>
    private Guid AdminUserId()
    {
        var token = new JwtSecurityTokenHandler().ReadJwtToken(fixture.AdminToken);
        string? sub = token.Claims.FirstOrDefault(c =>
            c.Type is "sub" or "nameid"
                   or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

        Guid.TryParse(sub, out Guid id).Should().BeTrue("the token identifies the signed-in user");
        return id;
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ConfirmUrl);
        if (code is not null)
            request.Headers.Add("Cookie", $"{CookieName}={code}");

        return await client.SendAsync(request);
    }

    private SamlLinkConfirmations Confirmations() =>
        fixture.Factory.Services.GetRequiredService<SamlLinkConfirmations>();

    private async Task<string?> LinkedDirectoryUserIdAsync(Guid userId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
        return await store.GetDirectoryUserIdAsync(userId);
    }

    [Fact]
    public async Task Confirm_WhenAnotherUserStartedTheSignIn_SavesNothing()
    {
        // The attack, end to end. Somebody else began the sign-in; this browser finished it. The
        // assertion was genuine and every check on it passed — the forgery is in the pairing.
        Guid admin = AdminUserId();
        Guid someoneElse = Guid.NewGuid();

        string code = Confirmations().Start(new PendingIdentityLink(
            someoneElse, "victim-directory-id", "victim", "victim@example.com"));

        using var client = AdminClientNoRedirect();
        var response = await ConfirmAsync(client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error=aws_wrong_user");

        (await LinkedDirectoryUserIdAsync(admin))
            .Should().BeNull("a link must not be saved for a sign-in this user did not start");
    }

    [Fact]
    public async Task Confirm_WithNoCookie_SavesNothing()
    {
        // What an attacker's own browser has. The code went to whoever completed the sign-in, and
        // it is HttpOnly, so it never reaches script or a URL somebody could pass on.
        using var client = AdminClientNoRedirect();
        var response = await ConfirmAsync(client, code: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error=aws_unknown_request");
    }

    [Fact]
    public async Task Confirm_WithACodeNobodyIssued_SavesNothing()
    {
        using var client = AdminClientNoRedirect();
        var response = await ConfirmAsync(client, "not-a-code-this-deployment-issued");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("error=aws_unknown_request");
    }

    [Fact]
    public async Task Confirm_Unauthenticated_DoesNotConsumeTheClaim()
    {
        // Signing in is what supplies the other half of the pairing, so an anonymous request must
        // leave the claim intact rather than burning it.
        string code = Confirmations().Start(new PendingIdentityLink(
            Guid.NewGuid(), "dir-1", "person", null));

        using var anonymous = fixture.Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await ConfirmAsync(anonymous, code);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Confirmations().Consume(code).Should().NotBeNull("an anonymous attempt must not burn it");
    }

    [Fact]
    public async Task Confirm_ByTheUserWhoStartedIt_SavesTheLink()
    {
        // The ordinary flow, which must still work.
        Guid admin = AdminUserId();
        string directoryUserId = $"dir-{Guid.NewGuid():N}";

        string code = Confirmations().Start(new PendingIdentityLink(
            admin, directoryUserId, "admin-person", "admin@example.com"));

        using var client = AdminClientNoRedirect();
        try
        {
            var response = await ConfirmAsync(client, code);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Be("/profile/integrations");
            (await LinkedDirectoryUserIdAsync(admin)).Should().Be(directoryUserId);
        }
        finally
        {
            // The fixture is shared, and a neighbouring test asserts the admin has no linked
            // identities. Leaving this row behind would fail it from a distance.
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.DeleteAsync(admin);
        }
    }

    [Fact]
    public async Task Confirm_Twice_SavesOnlyOnce()
    {
        Guid admin = AdminUserId();
        string code = Confirmations().Start(new PendingIdentityLink(
            admin, $"dir-{Guid.NewGuid():N}", "admin-person", null));

        using var client = AdminClientNoRedirect();
        try
        {
            (await ConfirmAsync(client, code)).Headers.Location!.OriginalString
                .Should().Be("/profile/integrations");

            var second = await ConfirmAsync(client, code);
            second.Headers.Location!.OriginalString.Should().Contain("error=aws_unknown_request");
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AwsIdentityLinkStore>();
            await store.DeleteAsync(admin);
        }
    }
}
