using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Users;

/// <summary>Why a user operation didn't succeed.</summary>
public enum UserStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "user-not-found", "User not found")]
    NotFound,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,

    [Failure(400, "invalid-roles", "Invalid roles")]
    InvalidRoles,

    [Failure(500, "user-not-changed", "The user wasn't changed")]
    NotChanged,

    [Failure(409, "user-changed", "The user changed")]
    Changed,
}

/// <summary>One page of users. <see cref="Take"/> null means every user, which only the User management page asks for.</summary>
public sealed record ListUsersQuery(int Skip = 0, int? Take = null);

/// <summary>Users oldest first, how many there are, and whether another page follows.</summary>
public sealed record UserList(IReadOnlyList<UserAccount> Items, int TotalCount, bool HasMore);

/// <summary>The roles to give a user, replacing every role they hold.</summary>
public sealed record SetUserRolesRequest(Guid UserId, IReadOnlyList<string> Roles);

/// <summary>
/// Who may manage whom, the same on every surface. An administrator manages people's accounts,
/// but never their own (so nobody locks themselves out) and never the Owner's; only the Owner
/// manages another administrator.
/// </summary>
internal static class UserRules
{
    /// <summary>The roles a user can be given. Owner is the instance's own, and Agent belongs to agents, never people.</summary>
    public static readonly string[] Assignable = ["Viewer", "Editor", "Admin"];

    public static Guid? CallerId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    /// <summary>Why the caller may not manage <paramref name="target"/>, or null when they may.</summary>
    public static string? WhyNot(Guid? callerId, bool callerIsOwner, UserAccount target)
    {
        if (target.Id == callerId)
            return "You can't change your own account here.";
        if (target.Roles.Contains("Owner", StringComparer.OrdinalIgnoreCase))
            return "The Owner's account can't be changed here.";
        if (!callerIsOwner && target.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
            return "Only the Owner can manage Admins.";
        return null;
    }
}

/// <summary>Lists users, administrators only: every account, with its roles and when it last signed in.</summary>
[Operation("users.list", Scope = "admin:users", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListUsers(IUserDirectory directory, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<UserList, UserStatus>> RunAsync(OperationContext context, ListUsersQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin) || !await authorizer.AllowsAsync(context.User, Policies.AdminUsersScope))
            return Attempt<UserList, UserStatus>.Fail(UserStatus.Forbidden, "You don't have permission to manage users.");
        if (query.Skip < 0)
            return Attempt<UserList, UserStatus>.Fail(UserStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return Attempt<UserList, UserStatus>.Fail(UserStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return Attempt<UserList, UserStatus>.Fail(UserStatus.InvalidPage, $"take must be <= {MaxTake}");

        var page = await directory.ListAsync(query.Skip, query.Take, ct);
        bool hasMore = query.Take is int take && query.Skip + take < page.TotalCount;
        return Attempt<UserList, UserStatus>.Succeed(new UserList(page.Users, page.TotalCount, hasMore));
    }
}

/// <summary>
/// Replaces a user's roles. Administrators only, within <see cref="UserRules"/>: Owner and Agent are
/// never given, Admin only by the Owner, and a user keeps at least one role.
/// </summary>
[Operation("users.setRoles", Scope = "admin:users", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class SetUserRoles(IUserDirectory directory, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string UpdatedEvent = "user.roles.updated";

    public async Task<Attempt<UserAccount, UserStatus>> RunAsync(OperationContext context, SetUserRolesRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin) || !await authorizer.AllowsAsync(context.User, Policies.AdminUsersScope))
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Forbidden, "You don't have permission to manage users.");
        if (await directory.GetAsync(request.UserId, ct) is not { } target)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.NotFound, "User not found");

        bool callerIsOwner = await authorizer.AllowsAsync(context.User, Policies.Owner);
        if (UserRules.WhyNot(UserRules.CallerId(context.User), callerIsOwner, target) is { } whyNot)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Forbidden, whyNot);

        var roles = (request.Roles ?? []).Select(r => r?.Trim() ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roles.Count == 0)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.InvalidRoles, "At least one role is required.", "roles");
        if (roles.FirstOrDefault(r => r.Equals("Owner", StringComparison.OrdinalIgnoreCase) || r.Equals("Agent", StringComparison.OrdinalIgnoreCase)) is { } reserved)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.InvalidRoles, $"The '{reserved}' role cannot be assigned via this endpoint.", "roles");
        if (roles.FirstOrDefault(r => !UserRules.Assignable.Contains(r, StringComparer.OrdinalIgnoreCase)) is { } unknown)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.InvalidRoles,
                $"'{unknown}' is not a role. Roles: {string.Join(", ", UserRules.Assignable)}.", "roles");
        if (!callerIsOwner && roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Forbidden, "Only the Owner can assign the Admin role.");

        // The names Identity knows, so a role given in another case is stored as it is defined.
        roles = roles.Select(r => UserRules.Assignable.First(a => a.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();
        var change = await directory.SetRolesAsync(target.Id, target.Roles, roles, ct);
        if (change.Outcome == DirectoryWrite.Changed)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Changed, "The user changed while this was being checked. Load it again and retry.");
        if (change.Outcome == DirectoryWrite.Failed)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.NotChanged, string.Join(" ", change.Errors));

        await audit.LogAsync(UpdatedEvent, "user", target.Id.ToString(), new { Roles = roles }, ct);
        return Attempt<UserAccount, UserStatus>.Succeed(target with { Roles = roles });
    }
}

/// <summary>
/// Deletes a user, administrators only, within <see cref="UserRules"/>. Custom providers keep their
/// links in their own source servers, so each is owed an unlink: queued first but held until the
/// deletion succeeds, so a crash can't lose it and a failed deletion never unlinks a user who still
/// exists. The removal job settles anything left waiting.
/// </summary>
[Operation("users.delete", Scope = "admin:users", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteUser(IUserDirectory directory, ICustomLinkRemovalStore linkRemovals, IOperationAuthorizer authorizer, IAuditLogger audit)
{
    public const string DeletedEvent = "user.deleted";

    public async Task<Attempt<UserAccount, UserStatus>> RunAsync(OperationContext context, Guid userId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin) || !await authorizer.AllowsAsync(context.User, Policies.AdminUsersScope))
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Forbidden, "You don't have permission to manage users.");
        if (await directory.GetAsync(userId, ct) is not { } target)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.NotFound, "User not found");

        bool callerIsOwner = await authorizer.AllowsAsync(context.User, Policies.Owner);
        if (UserRules.WhyNot(UserRules.CallerId(context.User), callerIsOwner, target) is { } whyNot)
            return Attempt<UserAccount, UserStatus>.Fail(UserStatus.Forbidden, whyNot);

        await linkRemovals.EnqueueForUserAsync(target.Id, ct);
        var deleted = await directory.DeleteAsync(target.Id, target.Roles, ct);
        if (deleted != DirectoryWrite.Done)
        {
            // Cancelled only while the user still exists: if another request deleted them first, the unlinks
            // queued here are theirs too, and its release (or the removal job) still owes them.
            if (await directory.GetAsync(target.Id, ct) is not null)
                await linkRemovals.CancelForUserAsync(target.Id, ct: ct);
            return deleted == DirectoryWrite.Changed
                ? Attempt<UserAccount, UserStatus>.Fail(UserStatus.Changed, "The user changed while this was being checked. Load it again and retry.")
                : Attempt<UserAccount, UserStatus>.Fail(UserStatus.NotChanged, "Failed to delete user.");
        }
        await linkRemovals.ActivateForUserAsync(target.Id, ct);

        await audit.LogAsync(DeletedEvent, "user", target.Id.ToString(), null, ct);
        return Attempt<UserAccount, UserStatus>.Succeed(target);
    }
}

/// <summary>Every user operation in one place, for the User management page.</summary>
public sealed class UserOperations(ListUsers list, SetUserRoles setRoles, DeleteUser delete)
{
    public ListUsers List { get; } = list;

    public SetUserRoles SetRoles { get; } = setRoles;

    public DeleteUser Delete { get; } = delete;
}
