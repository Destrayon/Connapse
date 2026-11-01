namespace Connapse.Application.Ports;

/// <summary>A person's account as the user operations see it.</summary>
public sealed record UserAccount(
    Guid Id, string Email, string? DisplayName, IReadOnlyList<string> Roles, DateTime CreatedAt, DateTime? LastLoginAt);

/// <summary>One page of accounts, oldest first, and how many there are in all.</summary>
public sealed record DirectoryPage(IReadOnlyList<UserAccount> Users, int TotalCount);

/// <summary>How a write to an account went.</summary>
public enum DirectoryWrite
{
    Done,

    /// <summary>The account is gone, or no longer holds the roles the caller's rights were checked against; nothing was written.</summary>
    Changed,

    /// <summary>Identity refused; the account is as it was.</summary>
    Failed,
}

/// <summary>A role change's outcome, and Identity's reasons when it refused.</summary>
public sealed record RoleChange(DirectoryWrite Outcome, IReadOnlyList<string> Errors);

/// <summary>
/// The accounts the user operations manage. Web answers it with ASP.NET Core Identity, which
/// Connapse.Application can't reference; the operations own every rule about who may do what.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Accounts oldest first; <paramref name="take"/> null means every one from <paramref name="skip"/> on.</summary>
    Task<DirectoryPage> ListAsync(int skip, int? take, CancellationToken ct = default);

    Task<UserAccount?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Replaces every role the account holds with <paramref name="roles"/>, but only while it still holds
    /// exactly <paramref name="checkedRoles"/>, the roles the caller's rights were checked against. A change
    /// Identity refuses partway leaves the old roles in place.
    /// </summary>
    Task<RoleChange> SetRolesAsync(Guid id, IReadOnlyList<string> checkedRoles, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>Deletes the account, but only while it still holds exactly <paramref name="checkedRoles"/>.</summary>
    Task<DirectoryWrite> DeleteAsync(Guid id, IReadOnlyList<string> checkedRoles, CancellationToken ct = default);
}
