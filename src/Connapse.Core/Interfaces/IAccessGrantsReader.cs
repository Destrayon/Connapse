namespace Connapse.Core;

/// <summary>Who a grant was made to.</summary>
/// <param name="IsGroup">
/// True for a directory group, false for a directory user. The two are separate queries: the
/// grantee filter matches the grant record literally and does not expand membership, so a user's
/// group-held grants are only found by asking for each group by name.
/// </param>
/// <param name="Id">The identity store id of the user or group.</param>
public readonly record struct AccessGrantee(bool IsGroup, string Id);

/// <summary>One S3 access grant, as Connapse needs to read it.</summary>
/// <param name="GrantScope">
/// The location the grant permits, in the shape AWS reports it. Not usable as a prefix until it has
/// been through <c>GrantScope.Parse</c>, which reconciles the several forms AWS uses for the same
/// grant.
/// </param>
/// <param name="IsObjectScope">
/// True when the grant names a single object. Those match by equality, because as a prefix a grant
/// for <c>report.pdf</c> would also admit <c>report.pdf.bak</c>.
/// </param>
/// <param name="ApplicationArn">
/// The application the grantee may exercise this grant through, or null when the grant names none.
/// </param>
/// <remarks>
/// <see cref="ApplicationArn"/> is carried rather than filtered on in the query, and the difference
/// matters in both directions: filtering by application drops the grants that name none, which are
/// the common case, and ignoring the field admits grants that AWS would only honour somewhere else.
/// </remarks>
public record AccessGrantRecord(string GrantScope, bool IsObjectScope, string? ApplicationArn);

/// <summary>
/// Reads S3 Access Grants using Connapse's own AWS identity.
/// </summary>
/// <remarks>
/// Deliberately reads rather than exercises. Connapse never calls <c>GetDataAccess</c> and never
/// holds credentials belonging to the person searching — it reads the authorization AWS already
/// holds and applies it to its own index, which is where the documents actually are.
/// <para>
/// The AWS operation behind this, <c>ListAccessGrants</c>, is an administrative read over the whole
/// instance. AWS documents <c>ListCallerAccessGrants</c> for "what may I read", and that one
/// requires being the person — which is exactly the requirement this design exists to remove. Using
/// the administrative variant as an authorization oracle is therefore a decision Connapse takes on
/// its own authority, and AWS offers no guarantee that the two stay semantically aligned.
/// </para>
/// </remarks>
public interface IAccessGrantsReader
{
    /// <summary>Every grant held by <paramref name="grantee"/>.</summary>
    /// <remarks>
    /// Empty is a real answer — that person has been granted nothing — and must not be confused
    /// with a failure to ask, which has to deny rather than return nothing.
    /// </remarks>
    Task<IReadOnlyList<AccessGrantRecord>> ListForGranteeAsync(
        AccessGrantee grantee, CancellationToken ct = default);
}

/// <summary>
/// Which IAM Identity Center user a Connapse user proved they were.
/// </summary>
/// <remarks>
/// A Core interface so the scope resolver need not reference the identity layer to ask the one
/// question it has of it. The answer is an identity store id rather than a credential, because
/// there is no longer a credential to hold.
/// </remarks>
public interface IAwsIdentityLinkReader
{
    /// <summary>
    /// The directory user id linked to <paramref name="userId"/>, or null when they have connected
    /// none.
    /// </summary>
    Task<string?> GetDirectoryUserIdAsync(Guid userId, CancellationToken ct = default);
}
