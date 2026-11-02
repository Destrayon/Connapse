using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Connapse.Integration.Tests;

/// <summary>
/// The nightly API fuzz test (Schemathesis) got 500s from the sign-in routes for bodies and queries a client can
/// send: a missing field, a NUL character, a code that isn't base64url. A malformed request is the caller's
/// mistake, so each answers 4xx (CON-165).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class IdentityMalformedInputTests(SharedWebAppFixture fixture)
{
    public static TheoryData<string, object> MalformedBodies => new()
    {
        { "/api/v1/auth/token", new { email = "a\u0000b@example.com", password = "Whatever1!" } },
        { "/api/v1/identity/login", new { unrelated = true } },
        { "/api/v1/identity/login", new { email = "a\u0000b@example.com", password = "Whatever1!" } },
        { "/api/v1/identity/resetPassword", new { unrelated = true } },
        { "/api/v1/identity/resetPassword", new { email = "a\u0000b@example.com", resetCode = "x", newPassword = "Whatever1!" } },
        { "/api/v1/identity/forgotPassword", new { email = "a\u0000b@example.com" } },
        { "/api/v1/identity/resendConfirmationEmail", new { email = "a\u0000b@example.com" } },
        { "/api/v1/identity/refresh", new { refreshToken = "not-a-token" } },
        { "/api/v1/identity/refresh", new { unrelated = true } },
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Post_MalformedBody_IsAClientError(string route, object body)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(route, body);

        ((int)response.StatusCode).Should().BeInRange(400, 499, await response.Content.ReadAsStringAsync());
    }

    // CON-206: Schemathesis sent {"email": null}. JSON binding takes a null for a required string, so Identity
    // passed it on to a lookup that threw. A null field gets the validation problem a missing one gets.
    [Theory]
    [InlineData("/api/v1/identity/resendConfirmationEmail", """{"email":null}""", "Email")]
    [InlineData("/api/v1/identity/forgotPassword", """{"email":null}""", "Email")]
    [InlineData("/api/v1/identity/login", """{"email":null,"password":null}""", "Email")]
    [InlineData("/api/v1/identity/resetPassword", """{"email":null,"resetCode":"x","newPassword":"Whatever1!"}""", "Email")]
    [InlineData("/api/v1/identity/refresh", """{"refreshToken":null}""", "RefreshToken")]
    public async Task Post_NullRequiredField_IsAValidationProblem(string route, string json, string field)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.PostAsync(route, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        problem!.Errors.Should().ContainKey(field);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?userId=e&code=%C3%9B%C3%9C")]
    [InlineData("?userId=%F1%9C%AC%A8&code=%C3%9B%C3%9C&changedEmail=")]
    [InlineData("?userId=e%00&code=abc")]
    [InlineData("?userId=00000000-0000-0000-0000-000000000000&code=%24%C2%BD")]
    public async Task ConfirmEmail_MalformedQuery_IsAClientError(string query)
    {
        using var anonymous = fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/identity/confirmEmail" + query);

        ((int)response.StatusCode).Should().BeInRange(400, 499, await response.Content.ReadAsStringAsync());
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
