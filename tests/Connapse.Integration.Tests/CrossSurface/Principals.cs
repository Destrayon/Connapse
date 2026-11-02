using System.Collections.Concurrent;
using System.Net.Http.Json;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Who drives a case. The same principal runs it on every surface, so an under-scoped case asks
/// each surface the same question: may this user do this? A Viewer holds knowledge:read only, and
/// a user with no role holds no scope at all.
/// </summary>
public sealed record Principal(string Name, string Email, string Password, string? Role)
{
    public static readonly Principal Admin = new("admin", SharedWebAppFixture.AdminEmail, SharedWebAppFixture.AdminPassword, "Admin");
    public static readonly Principal Viewer = new("viewer", "viewer@cross-surface.connapse.io", "CrossSurfaceViewer1!", "Viewer");
    public static readonly Principal NoRole = new("no-role", "norole@cross-surface.connapse.io", "CrossSurfaceNoRole1!", null);

    public override string ToString() => Name;
}

/// <summary>Seeds each principal once in the shared host and keeps its access token.</summary>
public static class Principals
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Tokens = new(StringComparer.Ordinal);

    /// <summary>The principal's bearer token, seeding the user first if it doesn't exist yet.</summary>
    public static Task<string> TokenAsync(SharedWebAppFixture fixture, Principal principal) =>
        principal == Principal.Admin
            ? Task.FromResult(fixture.AdminToken)
            : Tokens.GetOrAdd(principal.Email, _ => new Lazy<Task<string>>(() => SeedAndSignInAsync(fixture, principal))).Value;

    /// <summary>The principal's user id, for matching audit rows.</summary>
    public static async Task<Guid> UserIdAsync(SharedWebAppFixture fixture, Principal principal)
    {
        await TokenAsync(fixture, principal);
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        return (await users.FindByEmailAsync(principal.Email))!.Id;
    }

    private static async Task<string> SeedAndSignInAsync(SharedWebAppFixture fixture, Principal principal)
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
            if (await users.FindByEmailAsync(principal.Email) is null)
            {
                var user = new ConnapseUser
                {
                    UserName = principal.Email,
                    Email = principal.Email,
                    EmailConfirmed = true,
                    DisplayName = principal.Name,
                    CreatedAt = DateTime.UtcNow,
                };
                var created = await users.CreateAsync(user, principal.Password);
                if (!created.Succeeded)
                    throw new InvalidOperationException(string.Join(", ", created.Errors.Select(e => e.Description)));
                if (principal.Role is not null)
                    await users.AddToRoleAsync(user, principal.Role);
            }
        }

        using var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/token", new { email = principal.Email, password = principal.Password });
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenBody>();
        return token!.AccessToken;
    }

    private sealed record TokenBody(string AccessToken);
}
