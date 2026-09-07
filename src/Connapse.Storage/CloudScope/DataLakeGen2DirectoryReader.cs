using Azure;
using Azure.Core;
using Azure.Storage.Files.DataLake;
using Azure.Storage.Files.DataLake.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Reads a Gen2 directory's access ACL live over the ADLS SDK, with Connapse's own
/// <see cref="TokenCredential"/> (Storage Blob Data Reader). A thin shell over the SDK: every
/// translation lives in the <c>internal static</c> mappers, which are unit-tested directly. Any SDK
/// failure returns <c>null</c> (the resolver treats that as fail-closed); genuine caller
/// cancellation propagates.
/// </summary>
public sealed class DataLakeGen2DirectoryReader(TokenCredential credential) : IGen2DirectoryReader
{
    public async Task<Gen2ModeBits?> ReadModeBitsAsync(Gen2Path directory, CancellationToken ct = default)
    {
        try
        {
            DataLakeDirectoryClient dir = DirectoryClient(directory);
            Response<PathAccessControl> ac = await dir.GetAccessControlAsync(cancellationToken: ct);
            // GetAccessControl returns the full ACL; derive the cheap mode-bits view from it. (The
            // GetPaths-listing optimization is a Phase-4e pre-warm concern; correctness does not
            // depend on it.) The extended flag is "any named entry or mask present".
            Gen2Acl acl = MapAccessControl(ac.Value.Owner, ac.Value.Group, ac.Value.AccessControlList);
            bool extended = acl.NamedUsers.Count > 0 || acl.NamedGroups.Count > 0 || acl.Mask is not null;
            return new Gen2ModeBits(acl.OwnerOid, acl.OwningGroupOid,
                acl.OwnerPermissions, acl.OwningGroupPermissions, acl.OtherPermissions, extended);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<Gen2Acl?> ReadAccessAclAsync(Gen2Path directory, CancellationToken ct = default)
    {
        try
        {
            DataLakeDirectoryClient dir = DirectoryClient(directory);
            Response<PathAccessControl> ac = await dir.GetAccessControlAsync(cancellationToken: ct);
            return MapAccessControl(ac.Value.Owner, ac.Value.Group, ac.Value.AccessControlList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private DataLakeDirectoryClient DirectoryClient(Gen2Path directory)
    {
        var service = new DataLakeServiceClient(
            new Uri($"https://{directory.Account}.dfs.core.windows.net"), credential);
        DataLakeFileSystemClient fs = service.GetFileSystemClient(directory.FileSystem);
        return directory.Path.Length == 0
            ? fs.GetDirectoryClient("/")
            : fs.GetDirectoryClient(directory.Path);
    }

    /// <summary>Parses an SDK owner/group/symbolic-permissions triple into mode bits.</summary>
    internal static Gen2ModeBits MapModeBits(string? owner, string? group, string symbolicPermissions)
    {
        var (o, g, other, extended) = Gen2Permissions.ParseSymbolic(symbolicPermissions);
        return new Gen2ModeBits(owner, group, o, g, other, extended);
    }

    /// <summary>Builds a <see cref="Gen2Acl"/> from the SDK access-control list, ignoring default
    /// (inheritance) entries and keeping only the access-scope ones.</summary>
    internal static Gen2Acl MapAccessControl(string? owner, string? group, IEnumerable<PathAccessControlItem> items)
    {
        Gen2Permission ownerPerms = Gen2Permission.None, groupPerms = Gen2Permission.None, other = Gen2Permission.None;
        Gen2Permission? mask = null;
        var namedUsers = new List<Gen2NamedAce>();
        var namedGroups = new List<Gen2NamedAce>();

        foreach (PathAccessControlItem item in items)
        {
            if (item.DefaultScope) continue; // inheritance ACL, not evaluated
            Gen2Permission perms = FromRolePermissions(item.Permissions);
            bool named = !string.IsNullOrEmpty(item.EntityId);
            switch (item.AccessControlType)
            {
                case AccessControlType.User when named: namedUsers.Add(new Gen2NamedAce(item.EntityId!, perms)); break;
                case AccessControlType.User: ownerPerms = perms; break;
                case AccessControlType.Group when named: namedGroups.Add(new Gen2NamedAce(item.EntityId!, perms)); break;
                case AccessControlType.Group: groupPerms = perms; break;
                case AccessControlType.Mask: mask = perms; break;
                case AccessControlType.Other: other = perms; break;
            }
        }

        return new Gen2Acl(owner, group, ownerPerms, groupPerms, other, mask, namedUsers, namedGroups);
    }

    private static Gen2Permission FromRolePermissions(RolePermissions p)
    {
        Gen2Permission result = Gen2Permission.None;
        if (p.HasFlag(RolePermissions.Read)) result |= Gen2Permission.Read;
        if (p.HasFlag(RolePermissions.Write)) result |= Gen2Permission.Write;
        if (p.HasFlag(RolePermissions.Execute)) result |= Gen2Permission.Execute;
        return result;
    }
}
