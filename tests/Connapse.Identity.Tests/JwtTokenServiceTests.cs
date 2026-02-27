using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class JwtTokenServiceTests
{
    private const string TestSecret = "test-jwt-secret-must-be-at-least-64-characters-long-for-hs256-ok!";

    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static JwtTokenService CreateService(ConnapseIdentityDbContext db, string? secret = null)
    {
        var settings = Options.Create(new JwtSettings
        {
            Secret = secret ?? TestSecret,
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            AccessTokenLifetimeMinutes = 60,
            RefreshTokenLifetimeDays = 7,
        });

        return new JwtTokenService(
            new OptionsMonitorAdapter(settings.Value),
            db,
            NullLogger<JwtTokenService>.Instance);
    }

    // ── Access token generation ──────────────────────────────────────────

    [Fact]
    public void GenerateAccessToken_ValidClaims_ReturnsNonEmptyString()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };

        var token = sut.GenerateAccessToken(claims);

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateAccessToken_WithEmailClaim_ClaimPresentInToken()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var email = "user@example.com";
        var claims = new[] { new Claim(ClaimTypes.Email, email) };

        var token = sut.GenerateAccessToken(claims);

        var handler = new JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(token);
        parsed.Claims.Should().Contain(c =>
            c.Type == ClaimTypes.Email && c.Value == email,
            because: "JWT should contain the email claim");
    }

    // ── Token validation ─────────────────────────────────────────────────

    [Fact]
    public void ValidateToken_ValidToken_ReturnsPrincipal()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-id-123") };
        var token = sut.GenerateAccessToken(claims);

        var principal = sut.ValidateToken(token);

        principal.Should().NotBeNull();
        principal!.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("user-id-123");
    }

    [Fact]
    public void ValidateToken_TamperedToken_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-id") };
        var token = sut.GenerateAccessToken(claims);
        var tampered = token[..^5] + "XXXXX"; // corrupt the signature

        var principal = sut.ValidateToken(tampered);

        principal.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_WrongSecret_ReturnsNull()
    {
        using var db = CreateDbContext();
        var issuer = CreateService(db, "secret-one-for-signing-must-be-64-chars-long-xxxxxxxxxxxxxxxxxx!");
        var validator = CreateService(db, "secret-two-for-validating-must-be-64-chars-long-xxxxxxxxxxxxxxxxx!");
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-id") };
        var token = issuer.GenerateAccessToken(claims);

        var principal = validator.ValidateToken(token);

        principal.Should().BeNull();
    }

    // ── Adapter to implement IOptionsMonitor<JwtSettings> ────────────────

    private sealed class OptionsMonitorAdapter(JwtSettings value) : IOptionsMonitor<JwtSettings>
    {
        public JwtSettings CurrentValue { get; } = value;
        public JwtSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<JwtSettings, string?> listener) => null;
    }
}
