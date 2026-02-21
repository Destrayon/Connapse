using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Connapse.Identity.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Identity.Authentication;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var apiKeyHeader))
            return AuthenticateResult.NoResult();

        var apiKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return AuthenticateResult.Fail("API key is empty.");

        if (!apiKey.StartsWith(ApiKeyAuthenticationOptions.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.Fail("Invalid API key format.");

        // Hash the token for lookup
        var tokenHash = ComputeSha256Hash(apiKey);

        // Create a scope to resolve scoped services
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();

        var pat = await dbContext.PersonalAccessTokens
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.TokenHash == tokenHash);

        if (pat is null)
            return AuthenticateResult.Fail("Invalid API key.");

        if (pat.RevokedAt is not null)
            return AuthenticateResult.Fail("API key has been revoked.");

        if (pat.ExpiresAt is not null && pat.ExpiresAt < DateTime.UtcNow)
            return AuthenticateResult.Fail("API key has expired.");

        // Fire-and-forget: update last used timestamp
        _ = Task.Run(async () =>
        {
            try
            {
                using var updateScope = serviceProvider.CreateScope();
                var updateDb = updateScope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
                await updateDb.PersonalAccessTokens
                    .Where(p => p.Id == pat.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastUsedAt, DateTime.UtcNow));
            }
            catch
            {
                // Best-effort update, don't fail auth
            }
        });

        // Build claims
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, pat.UserId.ToString()),
            new(ClaimTypes.Name, pat.User.UserName ?? pat.User.Email ?? ""),
            new(ClaimTypes.Email, pat.User.Email ?? ""),
            new("auth_method", "api_key"),
            new("pat_id", pat.Id.ToString()),
        };

        // Add scope claims
        if (!string.IsNullOrWhiteSpace(pat.Scopes))
        {
            foreach (var patScope in pat.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("scope", patScope));
            }
        }

        // Add role claims from user
        var userRoles = await dbContext.UserRoles
            .Where(ur => ur.UserId == pat.UserId)
            .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .ToListAsync();

        foreach (var role in userRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, ApiKeyAuthenticationOptions.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationOptions.SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    internal static string ComputeSha256Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
