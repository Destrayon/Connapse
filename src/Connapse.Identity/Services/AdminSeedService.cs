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
    public static readonly string[] DefaultRoles = ["Owner", "Admin", "Editor", "Viewer"];

    private static readonly Dictionary<string, string> RoleDescriptions = new()
    {
        ["Owner"] = "Instance owner with full control including admin management",
        ["Admin"] = "Full system access including user management and settings",
        ["Editor"] = "Can read and write knowledge (upload, delete, organize)",
        ["Viewer"] = "Read-only access to knowledge and search",
    };

    public async Task SeedAsync()
    {
        await SeedRolesAsync();
        await SeedOwnerUserAsync();
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

    private async Task SeedOwnerUserAsync()
    {
        var adminEmail = configuration["CONNAPSE_ADMIN_EMAIL"];
        var adminPassword = configuration["CONNAPSE_ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogInformation(
                "Owner seed skipped: CONNAPSE_ADMIN_EMAIL and CONNAPSE_ADMIN_PASSWORD environment variables not set");
            return;
        }

        var existingUser = await userManager.FindByEmailAsync(adminEmail);
        if (existingUser is not null)
        {
            // Ensure existing user has Owner and Admin roles
            if (!await userManager.IsInRoleAsync(existingUser, "Owner"))
            {
                await userManager.AddToRoleAsync(existingUser, "Owner");
                logger.LogInformation("Added Owner role to existing admin user");
            }
            if (!await userManager.IsInRoleAsync(existingUser, "Admin"))
            {
                await userManager.AddToRoleAsync(existingUser, "Admin");
                logger.LogInformation("Added Admin role to existing admin user");
            }
            return;
        }

        var ownerUser = new ConnapseUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            DisplayName = "Instance Owner",
            IsSystemAdmin = true,
            CreatedAt = DateTime.UtcNow,
        };

        var createResult = await userManager.CreateAsync(ownerUser, adminPassword);
        if (!createResult.Succeeded)
        {
            logger.LogError("Failed to create owner user: {Errors}",
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(ownerUser, "Owner");
        var roleResult = await userManager.AddToRoleAsync(ownerUser, "Admin");
        if (roleResult.Succeeded)
            logger.LogInformation("Created owner user and assigned roles");
        else
            logger.LogError("Failed to assign roles to owner user: {Errors}",
                string.Join(", ", roleResult.Errors.Select(e => e.Description)));
    }
}
