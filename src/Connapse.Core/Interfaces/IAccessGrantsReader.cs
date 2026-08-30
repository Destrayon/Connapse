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
    /// <param name="region">
    /// Which Access Grants instance to ask. Grants are created against the instance in the bucket's
    /// region, so there is no single place to look — a deployment with buckets in two regions has
    /// its grants in two instances, and asking only one hides documents the person was granted.
    /// </param>
    Task<IReadOnlyList<AccessGrantRecord>> ListForGranteeAsync(
        AccessGrantee grantee, string region, CancellationToken ct = default);

    /// <summary>
    /// The scope of every grant in the instance, whoever holds it.
    /// </summary>
    /// <remarks>
    /// For answering "is anybody granted anything here at all", which is what makes a connection
    /// naming an ungranted bucket worth warning about. Deliberately not per-person: the question is
    /// whether the bucket is reachable by anyone, and asking it once for a page of connections
    /// costs one call rather than one per connection per viewer.
    /// <para>
    /// Not usable for deciding what a search may read. It ignores who holds each grant, so treating
    /// it as an authorization answer would show everybody everything anyone was granted. That is
    /// <see cref="ListForGranteeAsync"/>'s job and the two must not be confused.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> ListAllScopesAsync(string region, CancellationToken ct = default);
}

/// <summary>
/// The AWS regions Connapse has S3 data in, and therefore has to look for grants in.
/// </summary>
/// <remarks>
/// Taken from the connections rather than from a setting, because it is the same answer and one of
/// them would go stale. Asking every AWS region instead would be dozens of calls on every search
/// resolution to find grants in two.
/// </remarks>
public interface IAwsGrantRegions
{
    /// <summary>Distinct regions of the configured S3 connections.</summary>
    /// <remarks>
    /// Empty when nothing is connected, which is a real answer: there are no buckets, so there is
    /// nowhere to look and nothing to find.
    /// </remarks>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);
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
