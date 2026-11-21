namespace Connapse.Identity.Services;

public class JwtSettings
{
    public const string SectionName = "Identity:Jwt";

    public string? Secret { get; set; }
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    // Idle lifetime: each refresh issues a token good for this long again, so a client in use stays signed in and
    // one left unused this long signs in again. Auth0's rotating refresh tokens default to 30 days too (CON-360).
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    // How long a refresh token that was just rotated may be redeemed again, for a client that refreshed twice at
    // once (two sessions sharing one sign-in) or lost the response. Outside it a reused token is treated as stolen
    // and its whole family is revoked. Okta's default is 30 seconds; capped at 60 (CON-360).
    public int RefreshTokenReuseIntervalSeconds { get; set; } = 30;

    public const int MaxRefreshTokenReuseIntervalSeconds = 60;
    public string Issuer { get; set; } = "Connapse";
    public string Audience { get; set; } = "Connapse";
}
