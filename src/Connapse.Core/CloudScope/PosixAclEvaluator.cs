// src/Connapse.Core/CloudScope/PosixAclEvaluator.cs
namespace Connapse.Core;

/// <summary>
/// The documented POSIX ACL first-match check, as ADLS Gen2 applies it: class precedence
/// owner &gt; named-user &gt; group-union &gt; other. The mask caps named users, the owning group,
/// and named groups — never the owner or "other". A matched owner or named-user class is terminal
/// (even when it denies); in the group class, any one matching entry that (masked) grants suffices.
/// Pure and side-effect-free so it is exhaustively unit-testable.
/// <para>
/// The requester's own object id and their group object ids are <b>separate</b> parameters, matched
/// against separate ACL classes: the owner and named-<i>user</i> classes only ever match the
/// requester's own <paramref name="userOid"/>, and the owning-group and named-<i>group</i> classes
/// only ever match <paramref name="groupOids"/>. Conflating them into one set would let group
/// membership satisfy a user-typed ACE (e.g. an anomalous <c>user:&lt;group-oid&gt;</c> entry) —
/// an over-grant relative to ADLS, which evaluates user entries by identity and group entries by
/// membership.
/// </para>
/// </summary>
public static class PosixAclEvaluator
{
    public static bool Grants(Gen2Acl acl, string userOid, IReadOnlySet<string> groupOids, Gen2Permission requested)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(userOid);
        ArgumentNullException.ThrowIfNull(groupOids);

        // 1. Owner class — matched ONLY by the requester's own user oid; terminal, mask does not apply.
        if (acl.OwnerOid is not null && acl.OwnerOid == userOid)
            return Has(acl.OwnerPermissions, requested);

        // 2. Named-user class — matched ONLY by the requester's own user oid; terminal, capped by mask.
        foreach (Gen2NamedAce entry in acl.NamedUsers)
        {
            if (entry.Oid == userOid)
                return Has(Capped(entry.Permissions, acl.Mask), requested);
        }

        // 3. Group class — owning group ∪ named groups, matched ONLY by group membership. Any one
        //    that (masked) grants suffices; a match that grants nothing denies rather than falling
        //    through to "other".
        bool anyGroupMatch = false;
        if (acl.OwningGroupOid is not null && groupOids.Contains(acl.OwningGroupOid))
        {
            anyGroupMatch = true;
            if (Has(Capped(acl.OwningGroupPermissions, acl.Mask), requested))
                return true;
        }
        foreach (Gen2NamedAce entry in acl.NamedGroups)
        {
            if (!groupOids.Contains(entry.Oid)) continue;
            anyGroupMatch = true;
            if (Has(Capped(entry.Permissions, acl.Mask), requested))
                return true;
        }
        if (anyGroupMatch)
            return false;

        // 4. Other class.
        return Has(acl.OtherPermissions, requested);
    }

    private static Gen2Permission Capped(Gen2Permission perms, Gen2Permission? mask) =>
        mask is null ? perms : perms & mask.Value;

    private static bool Has(Gen2Permission perms, Gen2Permission requested) =>
        (perms & requested) == requested;
}
