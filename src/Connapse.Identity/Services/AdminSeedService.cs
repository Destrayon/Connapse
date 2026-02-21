using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class AdminSeedService(
    UserManager<ConnapseUser> userManager,
    RoleManager<ConnapseRole> roleManager,
    IConfiguration configuration,
    ILogger<AdminSeedService> logger)
{
    public static readonly string[] DefaultRoles = ["Admin", "Editor", "Viewer", "Agent"];

    private static readonly Dictionary<string, string> RoleDescriptions = new()
    {
        ["Admin"] = "Full system access including user management and settings",
        ["Editor"] = "Can read and write knowledge (upload, delete, organize)",
        ["Viewer"] = "Read-only access to knowledge and search",
        ["Agent"] = "Programmatic access for AI agents (read + ingest)",
    };

    public async Task SeedAsync()
    {
        await SeedRolesAsync();
        await SeedAdminUserAsync();
    }

    private async Task SeedRolesAsync()
    {
        foreach (var roleName in DefaultRoles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var role = new ConnapseRole
                {
                    Name = roleName,
                    Description = RoleDescriptions.GetValueOrDefault(roleName),
                    CreatedAt = DateTime.UtcNow,
                };

                var result = await roleManager.CreateAsync(role);
                if (result.Succeeded)
                    logger.LogInformation("Created role: {Role}", roleName);
                else
                    logger.LogError("Failed to create role {Role}: {Errors}", roleName,
                        string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    private async Task SeedAdminUserAsync()
    {
        var adminEmail = configuration["CONNAPSE_ADMIN_EMAIL"];
        var adminPassword = configuration["CONNAPSE_ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogInformation(
                "Admin seed skipped: CONNAPSE_ADMIN_EMAIL and CONNAPSE_ADMIN_PASSWORD environment variables not set");
            return;
        }

        var existingUser = await userManager.FindByEmailAsync(adminEmail);
        if (existingUser is not null)
        {
            // Ensure existing user has Admin role
            if (!await userManager.IsInRoleAsync(existingUser, "Admin"))
            {
                await userManager.AddToRoleAsync(existingUser, "Admin");
                logger.LogInformation("Added Admin role to existing user: {Email}", adminEmail);
            }
            return;
        }

        var adminUser = new ConnapseUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            DisplayName = "System Admin",
            IsSystemAdmin = true,
            CreatedAt = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(adminUser, adminPassword);
        if (!createResult.Succeeded)
        {
            logger.LogError("Failed to create admin user: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        var roleResult = await userManager.AddToRoleAsync(adminUser, "Admin");
        if (roleResult.Succeeded)
            logger.LogInformation("Created admin user: {Email}", adminEmail);
        else
            logger.LogError("Failed to assign Admin role to {Email}: {Errors}", adminEmail,
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));
    }
}
