using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.CustomSource.Example;

public sealed record ConfirmRequest(string? Code, string? ConnapseUserId);

public sealed record LinkStatus(bool Linked, string? DisplayName);

/// <summary>
/// The linking page Connapse sends a browser to, and the API Connapse uses to confirm and read links.
/// Choosing a name on the sign-in page stands in for signing in to the real target system.
/// </summary>
public static class LinkEndpoints
{
    private const string StateCookie = "example_link_state";

    public static void MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/link", StartLink);
        app.MapPost("/link/complete", CompleteLink);

        RouteGroupBuilder links = app.MapGroup("/v1/links").AddEndpointFilter<BearerTokenFilter>();
        links.MapPost("/confirm", Confirm);
        links.MapGet("/{connapseUserId}", Status);
        links.MapDelete("/{connapseUserId}", Unlink);
    }

    private static async Task<IResult> StartLink(HttpContext http, ExampleConfig config, LinkStore links)
    {
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (config.LinkSecret is null) return Results.NotFound();

        IFormCollection form = await http.Request.ReadFormAsync();
        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(
            form["token"].ToString(),
            new TokenValidationParameters
            {
                ValidIssuer = config.ConnapseIssuer,
                ValidAudience = config.ProviderId,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.LinkSecret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(60),
                RequireExpirationTime = true,
            });
        if (!result.IsValid) return BadRequest("The link token is not valid.");

        string? connapseUserId = result.ClaimsIdentity.FindFirst("sub")?.Value;
        string? tokenId = result.ClaimsIdentity.FindFirst("jti")?.Value;
        string? returnUrl = result.ClaimsIdentity.FindFirst("return_url")?.Value;
        if (string.IsNullOrEmpty(connapseUserId) || string.IsNullOrEmpty(tokenId)
            || !Uri.TryCreate(returnUrl, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
            return BadRequest("The link token is missing sub, jti or an absolute return_url.");

        DateTimeOffset expiresAt = new(result.SecurityToken.ValidTo, TimeSpan.Zero);
        if (!links.TryUseTokenId(tokenId, expiresAt)) return BadRequest("This link token was already used.");

        string state = links.StartSession(connapseUserId, returnUrl!);
        http.Response.Cookies.Append(StateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/link",
            MaxAge = TimeSpan.FromMinutes(5),
        });
        return Results.Content(SignInPage(state, config.Users), "text/html");
    }

    private static async Task<IResult> CompleteLink(HttpContext http, ExampleConfig config, LinkStore links)
    {
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        IFormCollection form = await http.Request.ReadFormAsync();
        string state = form["state"].ToString();
        string user = form["user"].ToString();
        string? cookie = http.Request.Cookies[StateCookie];

        // The state cookie proves this browser is the one that presented the link token.
        if (state.Length == 0 || cookie is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(state)))
            return BadRequest("This sign-in did not start in this browser.");

        LinkSession? session = links.TakeSession(state);
        if (session is null) return BadRequest("This sign-in expired; start again from Connapse.");
        if (!config.Users.Contains(user, StringComparer.Ordinal)) return BadRequest("Unknown user.");

        string code = links.AddPending(session.ConnapseUserId, user);
        http.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/link" });
        string separator = session.ReturnUrl.Contains('?') ? "&" : "?";
        return Results.Redirect($"{session.ReturnUrl}{separator}code={Uri.EscapeDataString(code)}");
    }

    private static IResult Confirm(ConfirmRequest request, ExampleConfig config, LinkStore links)
    {
        if (config.LinkSecret is null) return Results.NotFound();
        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.ConnapseUserId))
            return BadRequest("code and connapse_user_id are required.");

        return links.Confirm(request.Code, request.ConnapseUserId)
            ? Results.Ok(new LinkStatus(true, links.ExternalUser(request.ConnapseUserId)))
            : Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The code is unknown, expired, already used, or was issued for another user.");
    }

    private static IResult Status(string connapseUserId, LinkStore links)
    {
        string? user = links.ExternalUser(connapseUserId);
        return Results.Ok(new LinkStatus(user is not null, user));
    }

    private static IResult Unlink(string connapseUserId, LinkStore links)
    {
        links.Remove(connapseUserId);
        return Results.NoContent();
    }

    private static IResult BadRequest(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private static string SignInPage(string state, IReadOnlyList<string> users)
    {
        StringBuilder html = new("<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign in</title></head><body>");
        html.Append("<h1>Sign in to the example system</h1><p>Choose who you are in the example system.</p>");
        foreach (string user in users)
        {
            string name = WebUtility.HtmlEncode(user);
            html.Append("<form method=\"post\" action=\"/link/complete\">")
                .Append($"<input type=\"hidden\" name=\"state\" value=\"{WebUtility.HtmlEncode(state)}\">")
                .Append($"<input type=\"hidden\" name=\"user\" value=\"{name}\">")
                .Append($"<button type=\"submit\">{name}</button></form>");
        }
        return html.Append("</body></html>").ToString();
    }
}
