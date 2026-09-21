namespace Connapse.Core;

/// <summary>POSIX permission bits, as flags so a permission set is one value.</summary>
[Flags]
public enum Gen2Permission
{
    None = 0,
    Execute = 1,
    Write = 2,
    Read = 4,
}

/// <summary>A named ADLS ACL entry: a user or group principal object id and its permissions.</summary>
public record Gen2NamedAce(string Oid, Gen2Permission Permissions);

/// <summary>
/// One directory's <b>access</b> ACL (never the default/inheritance ACL), as the POSIX evaluator
/// needs it: the owner and owning-group principals and their permissions, the mask (present only
/// when the ACL is extended), the named user/group entries, and the "other" permissions.
/// </summary>
public record Gen2Acl(
    string? OwnerOid,
    string? OwningGroupOid,
    Gen2Permission OwnerPermissions,
    Gen2Permission OwningGroupPermissions,
    Gen2Permission OtherPermissions,
    Gen2Permission? Mask,
    IReadOnlyList<Gen2NamedAce> NamedUsers,
    IReadOnlyList<Gen2NamedAce> NamedGroups);

/// <summary>
/// The cheap "mode bits" view of a directory — owner/group principals plus the owner/group/other
/// rwx triples — from a listing (<c>GetPaths</c>) or properties read. <see cref="HasExtendedAcl"/>
/// is the POSIX '+' marker: when false there are no named entries and no mask, so these bits alone
/// decide access; when true a named entry could be decisive and the full ACL must be read.
/// </summary>
public record Gen2ModeBits(
    string? OwnerOid,
    string? OwningGroupOid,
    Gen2Permission Owner,
    Gen2Permission OwningGroup,
    Gen2Permission Other,
    bool HasExtendedAcl)
{
    /// <summary>The equivalent <see cref="Gen2Acl"/> with no named entries and no mask. Correct to
    /// evaluate against only when the requester owns the directory or when there is no extended ACL;
    /// the resolver enforces that precondition.</summary>
    public Gen2Acl ToAcl() =>
        new(OwnerOid, OwningGroupOid, Owner, OwningGroup, Other, Mask: null, NamedUsers: [], NamedGroups: []);
}

/// <summary>An ADLS path: the storage account, filesystem (container), and the path within it
/// (no leading slash; the filesystem root is the empty string).</summary>
public record Gen2Path(string Account, string FileSystem, string Path);

/// <summary>Parses ADLS symbolic permission strings into <see cref="Gen2Permission"/> triples.</summary>
public static class Gen2Permissions
{
    /// <summary>Maps one rwx triple (e.g. 'r','-','x') to a permission set.</summary>
    public static Gen2Permission FromRwx(char r, char w, char x)
    {
        Gen2Permission p = Gen2Permission.None;
        if (r == 'r') p |= Gen2Permission.Read;
        if (w == 'w') p |= Gen2Permission.Write;
        if (x is 'x' or 's' or 't') p |= Gen2Permission.Execute; // setuid/sticky imply the x slot
        return p;
    }

    /// <summary>
    /// Parses a symbolic permission string into owner/group/other triples plus the extended-ACL
    /// flag. Accepts a 9-char body ("rwxr-x---"), an optional trailing '+' (extended ACL), and an
    /// optional leading file-type char ("drwx...").
    /// </summary>
    public static (Gen2Permission Owner, Gen2Permission Group, Gen2Permission Other, bool Extended) ParseSymbolic(string symbolic)
    {
        ArgumentNullException.ThrowIfNull(symbolic);
        bool extended = symbolic.EndsWith('+');
        string s = extended ? symbolic[..^1] : symbolic;
        if (s.Length == 10) s = s[1..];           // drop a leading file-type char
        if (s.Length != 9)
            throw new FormatException($"Unexpected ADLS permission string '{symbolic}'.");
        return (
            FromRwx(s[0], s[1], s[2]),
            FromRwx(s[3], s[4], s[5]),
            FromRwx(s[6], s[7], s[8]),
            extended);
    }
}
