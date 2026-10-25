namespace Connapse.Application.Ports;

/// <summary>A person's account as the user operations see it.</summary>
public sealed record UserAccount(
    Guid Id, string Email, string? DisplayName, IReadOnlyList<string> Roles, DateTime CreatedAt, DateTime? LastLoginAt);

/// <summary>One page of accounts, oldest first, and how many there are in all.</summary>
public sealed record DirectoryPage(IReadOnlyList<UserAccount> Users, int TotalCount);

/// <summary>
/// The accounts the user operations manage. Web answers it with ASP.NET Core Identity, which
/// Connapse.Application can't reference; the operations own every rule about who may do what.
/// </summary>
public interface IUserDirectory
{
    /// <summary>Accounts oldest first; <paramref name="take"/> null means every one from <paramref name="skip"/> on.</summary>
    Task<DirectoryPage> ListAsync(int skip, int? take, CancellationToken ct = default);

    Task<UserAccount?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Replaces every role the account holds with <paramref name="roles"/>; the reasons it couldn't, or none.</summary>
    Task<IReadOnlyList<string>> SetRolesAsync(Guid id, IReadOnlyList<string> roles, CancellationToken ct = default);

    /// <summary>Deletes the account; false when Identity refused.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
