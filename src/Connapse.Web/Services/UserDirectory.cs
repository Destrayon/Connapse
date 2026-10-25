using Connapse.Application.Ports;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Web.Services;

/// <summary>The user operations' accounts, through ASP.NET Core Identity.</summary>
public sealed class UserDirectory(UserManager<ConnapseUser> users, IDbContextFactory<ConnapseIdentityDbContext> factory) : IUserDirectory
{
    public async Task<DirectoryPage> ListAsync(int skip, int? take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        int total = await db.Users.CountAsync(ct);

        IQueryable<ConnapseUser> query = db.Users.OrderBy(u => u.CreatedAt).Skip(skip);
        if (take is int pageSize)
            query = query.Take(pageSize);
        var page = await query.ToListAsync(ct);

        // One query for this page's roles rather than one per user.
        var ids = page.Select(u => u.Id).ToList();
        var roles = (await db.UserRoles
                .Where(ur => ids.Contains(ur.UserId))
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name! })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.RoleName).ToList());

        return new DirectoryPage(page.Select(u => Of(u, roles.GetValueOrDefault(u.Id, []))).ToList(), total);
    }

    public async Task<UserAccount?> GetAsync(Guid id, CancellationToken ct = default) =>
        await users.FindByIdAsync(id.ToString()) is { } user ? Of(user, (await users.GetRolesAsync(user)).ToList()) : null;

    public async Task<IReadOnlyList<string>> SetRolesAsync(Guid id, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(id.ToString()) is not { } user)
            return ["User not found."];

        var current = await users.GetRolesAsync(user);
        var removed = current.Count == 0 ? IdentityResult.Success : await users.RemoveFromRolesAsync(user, current);
        if (!removed.Succeeded)
            return removed.Errors.Select(e => e.Description).ToList();
        var added = await users.AddToRolesAsync(user, roles);
        return added.Succeeded ? [] : added.Errors.Select(e => e.Description).ToList();
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        await users.FindByIdAsync(id.ToString()) is { } user && (await users.DeleteAsync(user)).Succeeded;

    private static UserAccount Of(ConnapseUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email ?? "", user.DisplayName, roles, user.CreatedAt, user.LastLoginAt);
}
