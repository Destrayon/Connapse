namespace Connapse.Identity.Services;

public class JwtSettings
{
    public const string SectionName = "Identity:Jwt";

    public string? Secret { get; set; }
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 7;
    public string Issuer { get; set; } = "Connapse";
    public string Audience { get; set; } = "Connapse";
}
