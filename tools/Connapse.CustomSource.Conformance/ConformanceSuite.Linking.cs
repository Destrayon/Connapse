using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.CustomSource.Conformance;

public sealed partial class ConformanceSuite
{
    private Uri? _linkUrl;
    private string? _acceptedToken;

    private async Task RunLinkingChecksAsync(CancellationToken ct)
    {
        await RunCheckAsync("links.unknown-user", "An unlinked user's status is linked: false", LinkStatusUnknownAsync, ct);
        await RunCheckAsync("links.unknown-code", "Confirming a code the source server never issued is answered with 409", ConfirmUnknownCodeAsync, ct);
        await RunCheckAsync("links.unlink-idempotent", "Unlinking a user with no link is answered with 204", UnlinkIdempotentAsync, ct);
        await RunCheckAsync("link.token-accepted", "The linking page accepts a valid link token", TokenAcceptedAsync, ct);
        await RunCheckAsync("link.token-replay", "The linking page refuses a link token it has already seen", TokenReplayAsync, ct);
        foreach ((string id, string title, Func<string> mint) in BadTokens())
            await RunCheckAsync(id, title, c => TokenRefusedAsync(mint, c), ct);
        await RunCheckAsync("link.other-user", "A code is refused when confirmed for another Connapse user, and is then spent", OtherUserAsync, ct);
        await RunCheckAsync("link.round-trip", "A confirmed link shows as linked, and unlinking removes it", LinkRoundTripAsync, ct);
    }

    private void RequireLinking()
    {
        RequireCheck();
        if (Permissions?["linking"] is not JsonObject linking) throw new CheckSkippedException("Discovery declares no permissions.linking.");
        if (OptionalString(linking, "link_url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
            throw new CheckFailedException("permissions.linking has no absolute link_url.");
        _linkUrl = parsed;
    }

    private void RequireLinkSecret()
    {
        RequireLinking();
        if (options.LinkSecret is null || options.ProviderId is null || options.Issuer is null)
            throw new CheckSkippedException("Link tokens need --link-secret, --provider-id and --issuer.");
    }

    private string LinksPath(string userId) => $"/v1/links/{Uri.EscapeDataString(userId)}";

    private async Task LinkStatusUnknownAsync(CancellationToken ct)
    {
        RequireLinking();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, LinksPath(UnknownUser()), ct);
        JsonObject body = await ExpectJsonAsync(response, HttpStatusCode.OK);
        if (body["linked"]?.GetValueKind() != JsonValueKind.False)
            throw new CheckFailedException("A user the source server cannot know is not reported as linked: false.");
    }

    private async Task ConfirmUnknownCodeAsync(CancellationToken ct)
    {
        RequireLinking();
        using HttpResponseMessage response = await ConfirmAsync("connapse-conformance-" + Guid.NewGuid().ToString("N"), UnknownUser(), ct);
        ExpectStatus(response, HttpStatusCode.Conflict, "Confirming an unknown code");
    }

    private async Task UnlinkIdempotentAsync(CancellationToken ct)
    {
        RequireLinking();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, LinksPath(UnknownUser()), ct);
        ExpectStatus(response, HttpStatusCode.NoContent, "Unlinking a user with no link");
    }

    private Task<HttpResponseMessage> ConfirmAsync(string code, string userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/v1/links/confirm", ct, new { code, connapseUserId = userId });

    private Uri ReturnUrl => new(options.Issuer!.TrimEnd('/') + $"/api/v1/auth/custom/{Uri.EscapeDataString(options.ProviderId!)}/return");

    private string Mint(string? secret = null, string? issuer = null, string? audience = null, TimeSpan? lifetime = null, string? subject = null)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now + (lifetime ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? options.Issuer,
            Audience = audience ?? options.ProviderId,
            IssuedAt = expires < now ? expires.AddMinutes(-5) : now,
            NotBefore = expires < now ? expires.AddMinutes(-5) : now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject ?? UnknownUser(),
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["return_url"] = ReturnUrl.ToString(),
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret ?? options.LinkSecret!)), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>A token claiming <c>alg: none</c>, with no signature, that a careless verifier might accept.</summary>
    private string MintUnsigned()
    {
        static string Encode(object value) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(new Dictionary<string, object>
        {
            ["iss"] = options.Issuer!,
            ["aud"] = options.ProviderId!,
            ["sub"] = UnknownUser(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 300,
            ["return_url"] = ReturnUrl.ToString(),
        })}.";
    }

    private IEnumerable<(string Id, string Title, Func<string> Mint)> BadTokens()
    {
        yield return ("link.token-bad-signature", "The linking page refuses a token signed with another secret",
            () => Mint(secret: "connapse-conformance-wrong-secret-0123456789abcdef"));
        yield return ("link.token-wrong-audience", "The linking page refuses a token for another provider",
            () => Mint(audience: "connapse-conformance-other-provider"));
        yield return ("link.token-wrong-issuer", "The linking page refuses a token from another issuer",
            () => Mint(issuer: "https://connapse-conformance.invalid"));
        yield return ("link.token-expired", "The linking page refuses an expired token",
            () => Mint(lifetime: TimeSpan.FromMinutes(-10)));
        yield return ("link.token-unsigned", "The linking page refuses a token with alg: none", MintUnsigned);
    }

    private async Task<HttpResponseMessage> PresentAsync(string token, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, _linkUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }),
        };
        return await http.SendAsync(request, ct);
    }

    private async Task TokenAcceptedAsync(CancellationToken ct)
    {
        RequireLinkSecret();
        string token = Mint();
        using HttpResponseMessage response = await PresentAsync(token, ct);
        if ((int)response.StatusCode >= 400)
            throw new CheckFailedException($"A valid link token was answered with {(int)response.StatusCode}. Check --link-secret, --provider-id and --issuer.");
        _acceptedToken = token;
    }

    private async Task TokenReplayAsync(CancellationToken ct)
    {
        RequireLinkSecret();
        string token = _acceptedToken ?? throw new CheckSkippedException("No token was accepted to replay.");
        using HttpResponseMessage response = await PresentAsync(token, ct);
        if ((int)response.StatusCode is < 400 or >= 500)
            throw new CheckFailedException($"A link token presented a second time was answered with {(int)response.StatusCode}, not refused.");
    }

    private async Task TokenRefusedAsync(Func<string> mint, CancellationToken ct)
    {
        RequireLinkSecret();
        using HttpResponseMessage response = await PresentAsync(mint(), ct);
        if ((int)response.StatusCode is < 400 or >= 500)
            throw new CheckFailedException($"The token was answered with {(int)response.StatusCode}, not refused with a 4xx.");
    }

    /// <summary>Starts a link for <paramref name="userId"/> and has the hook finish the middle man's sign-in.</summary>
    private async Task<string> SignInAsync(string userId, CancellationToken ct)
    {
        RequireLinkSecret();
        string? code = await _hooks.CompleteSignInAsync(_linkUrl!, Mint(subject: userId), ReturnUrl, ct);
        return string.IsNullOrEmpty(code) ? throw new CheckSkippedException("No hook to finish the sign-in (--sign-in-command).") : code;
    }

    private async Task<bool> LinkedAsync(string userId, CancellationToken ct)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, LinksPath(userId), ct);
        return (await ExpectJsonAsync(response, HttpStatusCode.OK))["linked"]?.GetValueKind() == JsonValueKind.True;
    }

    private async Task OtherUserAsync(CancellationToken ct)
    {
        string starter = UnknownUser(), other = UnknownUser();
        string code = await SignInAsync(starter, ct);

        using (HttpResponseMessage wrong = await ConfirmAsync(code, other, ct))
            ExpectStatus(wrong, HttpStatusCode.Conflict, "Confirming a code for a different Connapse user than the one who started it");
        using (HttpResponseMessage spent = await ConfirmAsync(code, starter, ct))
            ExpectStatus(spent, HttpStatusCode.Conflict, "Confirming a code a second time, after a refusal");
        if (await LinkedAsync(starter, ct) || await LinkedAsync(other, ct))
            throw new CheckFailedException("A refused confirmation left a user linked.");
    }

    private async Task LinkRoundTripAsync(CancellationToken ct)
    {
        string user = UnknownUser();
        string code = await SignInAsync(user, ct);
        try
        {
            using (HttpResponseMessage confirmed = await ConfirmAsync(code, user, ct))
            {
                JsonObject body = await ExpectJsonAsync(confirmed, HttpStatusCode.OK);
                if (body["linked"]?.GetValueKind() != JsonValueKind.True)
                    throw new CheckFailedException("A successful confirmation did not answer linked: true.");
            }

            if (!await LinkedAsync(user, ct)) throw new CheckFailedException("A confirmed link does not show as linked.");
        }
        finally
        {
            using HttpResponseMessage unlinked = await SendAsync(HttpMethod.Delete, LinksPath(user), ct);
            ExpectStatus(unlinked, HttpStatusCode.NoContent, "Unlinking a linked user");
        }

        if (await LinkedAsync(user, ct)) throw new CheckFailedException("After unlinking, the user still shows as linked.");
    }
}
