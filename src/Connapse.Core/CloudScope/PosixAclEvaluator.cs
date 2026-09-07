// src/Connapse.Core/CloudScope/PosixAclEvaluator.cs
namespace Connapse.Core;

/// <summary>
/// The documented POSIX ACL first-match check, as ADLS Gen2 applies it: class precedence
/// owner &gt; named-user &gt; group-union &gt; other. The mask caps named users, the owning group,
/// and named groups — never the owner or "other". A matched owner or named-user class is terminal
/// (even when it denies); in the group class, any one matching entry that (masked) grants suffices.
/// Pure and side-effect-free so it is exhaustively unit-testable.
/// </summary>
public static class PosixAclEvaluator
{
    public static bool Grants(Gen2Acl acl, IReadOnlySet<string> principals, Gen2Permission requested)
    {
        ArgumentNullException.ThrowIfNull(acl);
        ArgumentNullException.ThrowIfNull(principals);

        // 1. Owner class — terminal, mask does not apply.
        if (acl.OwnerOid is not null && principals.Contains(acl.OwnerOid))
            return Has(acl.OwnerPermissions, requested);

        // 2. Named-user class — terminal if matched, capped by the mask.
        foreach (Gen2NamedAce entry in acl.NamedUsers)
        {
            if (principals.Contains(entry.Oid))
                return Has(Capped(entry.Permissions, acl.Mask), requested);
        }

        // 3. Group class — owning group ∪ named groups. Any one that (masked) grants suffices; a
        //    match that grants nothing denies rather than falling through to "other".
        bool anyGroupMatch = false;
        if (acl.OwningGroupOid is not null && principals.Contains(acl.OwningGroupOid))
        {
            anyGroupMatch = true;
            if (Has(Capped(acl.OwningGroupPermissions, acl.Mask), requested))
                return true;
        }
        foreach (Gen2NamedAce entry in acl.NamedGroups)
        {
            if (!principals.Contains(entry.Oid)) continue;
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
