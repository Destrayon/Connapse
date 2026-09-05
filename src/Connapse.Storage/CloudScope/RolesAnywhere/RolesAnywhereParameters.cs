namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>
/// The identifiers a Roles Anywhere <c>CreateSession</c> call needs: which trust anchor vouches for
/// the certificate, which profile and role to assume, and where.
/// </summary>
public sealed record RolesAnywhereParameters(
    string TrustAnchorArn,
    string ProfileArn,
    string RoleArn,
    string Region,
    int? DurationSeconds = null,
    string? RoleSessionName = null);
