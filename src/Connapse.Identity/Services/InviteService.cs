using System.Security.Cryptography;
using System.Text;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connapse.Identity.Services;

public class InviteService(
    ConnapseIdentityDbContext dbContext,
    UserManager<ConnapseUser> userManager,
    ILogger<InviteService> logger)
{
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromDays(7);

    /// <summary>
    /// Creates an invitation and returns the raw token (to be included in the invite URL).
    /// </summary>
    public async Task<(string Token, UserInvitation Invitation)> CreateInviteAsync(
        string email, string role, Guid createdByUserId)
    {
        // Check if user already exists
        var existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
            throw new InvalidOperationException($"A user with email '{email}' already exists.");

        // Check for existing pending invite to same email
        var existingInvite = await dbContext.UserInvitations
            .FirstOrDefaultAsync(i => i.Email == email && i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow);
        if (existingInvite is not null)
            throw new InvalidOperationException($"A pending invitation for '{email}' already exists.");

        // Validate role
        if (!AdminSeedService.DefaultRoles.Contains(role))
            throw new ArgumentException($"Invalid role: {role}");

        // Owner role cannot be assigned via invitation
        if (role == "Owner")
            throw new InvalidOperationException("The Owner role cannot be assigned via invitation.");

        // Only Owners can invite Admins
        if (role == "Admin")
        {
            var inviter = await userManager.FindByIdAsync(createdByUserId.ToString());
            if (inviter is null || !await userManager.IsInRoleAsync(inviter, "Owner"))
                throw new InvalidOperationException("Only the Owner can invite users with the Admin role.");
        }

        var rawToken = GenerateToken();
        var tokenHash = HashToken(rawToken);

        var invitation = new UserInvitation
        {
            Email = email,
            Role = role,
            TokenHash = tokenHash,
            CreatedByUserId = createdByUserId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(DefaultExpiry),
        };

        dbContext.UserInvitations.Add(invitation);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Created invitation {InvitationId} for role {Role} by user {UserId}",
            invitation.Id, role, createdByUserId);

        return (rawToken, invitation);
    }

    /// <summary>
    /// Validates an invite token and returns the invitation if valid.
    /// </summary>
    public async Task<UserInvitation?> ValidateInviteAsync(string token)
    {
        var tokenHash = HashToken(token);

        return await dbContext.UserInvitations
            .FirstOrDefaultAsync(i =>
                i.TokenHash == tokenHash &&
                i.AcceptedAt == null &&
                i.ExpiresAt > DateTime.UtcNow);
    }

    /// <summary>
    /// Accepts an invitation: creates the user account with the assigned role.
    /// Returns the created user, or errors if the invite is invalid.
    /// </summary>
    public async Task<(ConnapseUser User, IdentityResult Result)> AcceptInviteAsync(
        string token, string password, string? displayName)
    {
        var invitation = await ValidateInviteAsync(token);
        if (invitation is null)
            return (null!, IdentityResult.Failed(new IdentityError
            {
                Code = "InvalidInvite",
                Description = "This invitation is invalid, expired, or has already been used."
            }));

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var displayNameTaken = await userManager.Users
                .AnyAsync(u => u.DisplayName == displayName);
            if (displayNameTaken)
                return (null!, IdentityResult.Failed(new IdentityError
                {
                    Code = "DuplicateDisplayName",
                    Description = "That display name is already taken."
                }));
        }

        var user = new ConnapseUser
        {
            UserName = invitation.Email,
            Email = invitation.Email,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            CreatedAt = DateTime.UtcNow,
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
            return (user, result);

        await userManager.AddToRoleAsync(user, invitation.Role);

        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.AcceptedByUserId = user.Id;
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Invitation accepted by {Email}, assigned role {Role}", invitation.Email, invitation.Role);

        return (user, result);
    }

    /// <summary>
    /// Returns all pending (not accepted, not expired) invitations.
    /// </summary>
    public async Task<List<UserInvitation>> ListPendingInvitesAsync()
    {
        return await dbContext.UserInvitations
            .Include(i => i.CreatedByUser)
            .Where(i => i.AcceptedAt == null && i.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Revokes a pending invitation by deleting it.
    /// </summary>
    public async Task<bool> RevokeInviteAsync(Guid invitationId)
    {
        var invitation = await dbContext.UserInvitations.FindAsync(invitationId);
        if (invitation is null || invitation.AcceptedAt is not null)
            return false;

        dbContext.UserInvitations.Remove(invitation);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Revoked invitation {InvitationId} for {Email}", invitationId, invitation.Email);
        return true;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }
}
