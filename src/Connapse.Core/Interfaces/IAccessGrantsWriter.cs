namespace Connapse.Core;

/// <summary>One location that could not be granted, and why.</summary>
public record GrantWriteFailure(string Location, string Reason);

/// <summary>The outcome of creating grants for one grantee against one region.</summary>
/// <param name="Created">Subprefixes a grant was created for.</param>
/// <param name="AlreadyGranted">Subprefixes a grant already covered (nothing created).</param>
/// <param name="Failed">Locations that could not be granted, with the AWS reason.</param>
/// <param name="AccessDenied">
/// True when a failure was AWS <c>AccessDenied</c> — Connapse's identity is not (yet) allowed to
/// create grants, so the UI should fall back to the admin-run CloudShell script rather than present
/// the failure as broken.
/// </param>
public record GrantWriteResult(
    IReadOnlyList<string> Created,
    IReadOnlyList<string> AlreadyGranted,
    IReadOnlyList<GrantWriteFailure> Failed,
    bool AccessDenied)
{
    /// <summary>Nothing failed.</summary>
    public bool Succeeded => Failed.Count == 0;

    /// <summary>An empty result — nothing requested, or the feature is not configured.</summary>
    public static readonly GrantWriteResult Nothing = new([], [], [], AccessDenied: false);
}

/// <summary>
/// Creates S3 Access Grants using Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// The write twin of <see cref="IAccessGrantsReader"/>. Connapse now creates grants as well as
/// reading them: its runtime identity is deliberately no longer read-only, so a compromise of that
/// identity can create grants. The blast radius is stated to the administrator on the setup page
/// (see <c>S3SetupPolicy.ManagedIdentitySummary</c>).
/// </remarks>
public interface IAccessGrantsWriter
{
    /// <summary>
    /// Creates one READ grant per location for <paramref name="grantee"/>, against the Access
    /// Grants instance in <paramref name="region"/>. Idempotent: existing grants are read first and
    /// skipped. Never creates WRITE or READWRITE.
    /// </summary>
    Task<GrantWriteResult> GrantReadAsync(
        AccessGrantee grantee, string region,
        IReadOnlyList<string> locations, CancellationToken ct = default);
}
