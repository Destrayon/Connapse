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

    public async Task<RoleChange> SetRolesAsync(
        Guid id, IReadOnlyList<string> checkedRoles, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        // Read again just before writing: if the roles changed since the caller's rights were checked
        // (another request made them an Admin, say), those rights may no longer hold.
        if (await users.FindByIdAsync(id.ToString()) is not { } user)
            return new RoleChange(DirectoryWrite.Changed, []);
        var current = (await users.GetRolesAsync(user)).ToList();
        if (!Same(current, checkedRoles))
            return new RoleChange(DirectoryWrite.Changed, []);

        var removed = current.Count == 0 ? IdentityResult.Success : await users.RemoveFromRolesAsync(user, current);
        if (!removed.Succeeded)
            return new RoleChange(DirectoryWrite.Failed, Errors(removed));
        IReadOnlyList<string> errors;
        try
        {
            var added = await users.AddToRolesAsync(user, roles);
            if (added.Succeeded)
                return new RoleChange(DirectoryWrite.Done, []);
            errors = Errors(added);
        }
        catch (InvalidOperationException)
        {
            // Identity throws, rather than failing, for a role it doesn't have.
            errors = ["A role in the change doesn't exist."];
        }

        // Put the old roles back, so a refused change never leaves the account with none.
        if (current.Count > 0)
            await users.AddToRolesAsync(user, current);
        return new RoleChange(DirectoryWrite.Failed, errors);
    }

    public async Task<DirectoryWrite> DeleteAsync(Guid id, IReadOnlyList<string> checkedRoles, CancellationToken ct = default)
    {
        if (await users.FindByIdAsync(id.ToString()) is not { } user || !Same(await users.GetRolesAsync(user), checkedRoles))
            return DirectoryWrite.Changed;
        return (await users.DeleteAsync(user)).Succeeded ? DirectoryWrite.Done : DirectoryWrite.Failed;
    }

    private static bool Same(IEnumerable<string> roles, IEnumerable<string> other) =>
        roles.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other);

    private static List<string> Errors(IdentityResult result) => result.Errors.Select(e => e.Description).ToList();

    private static UserAccount Of(ConnapseUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email ?? "", user.DisplayName, roles, user.CreatedAt, user.LastLoginAt);
}
