using Azure.Storage.Files.DataLake.Models;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class DataLakeGen2DirectoryReaderMappingTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;

    [Fact]
    public void MapModeBits_ParsesOwnerGroupOther_AndExtendedFlag()
    {
        Gen2ModeBits bits = DataLakeGen2DirectoryReader.MapModeBits("owner-oid", "group-oid", "rwxr-x---+");

        bits.OwnerOid.Should().Be("owner-oid");
        bits.OwningGroupOid.Should().Be("group-oid");
        bits.Owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
        bits.OwningGroup.Should().Be(RX);
        bits.Other.Should().Be(Gen2Permission.None);
        bits.HasExtendedAcl.Should().BeTrue();
    }

    [Fact]
    public void MapAccessControl_SplitsEntriesByType_AccessScopeOnly()
    {
        var items = new List<PathAccessControlItem>
        {
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.User,  RolePermissions.Read, defaultScope: false, entityId: "alice"),
            new(AccessControlType.Group, RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Group, RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: "devs"),
            new(AccessControlType.Mask,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Other, RolePermissions.None, defaultScope: false, entityId: null),
            // A default-scope entry must be ignored (inheritance ACL, not the access ACL).
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Write | RolePermissions.Execute, defaultScope: true, entityId: "should-be-ignored"),
        };

        Gen2Acl acl = DataLakeGen2DirectoryReader.MapAccessControl("owner-oid", "group-oid", items);

        acl.OwnerOid.Should().Be("owner-oid");
        acl.OwningGroupOid.Should().Be("group-oid");
        acl.OwnerPermissions.Should().Be(RX);
        acl.OwningGroupPermissions.Should().Be(Gen2Permission.Execute);
        acl.OtherPermissions.Should().Be(Gen2Permission.None);
        acl.Mask.Should().Be(RX);
        acl.NamedUsers.Should().ContainSingle().Which.Should().BeEquivalentTo(new Gen2NamedAce("alice", Gen2Permission.Read));
        acl.NamedGroups.Should().ContainSingle().Which.Should().BeEquivalentTo(new Gen2NamedAce("devs", RX));
    }

    [Fact]
    public void MapAccessControl_NoMaskEntry_LeavesMaskNull()
    {
        var items = new List<PathAccessControlItem>
        {
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Group, RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Other, RolePermissions.None, defaultScope: false, entityId: null),
        };

        DataLakeGen2DirectoryReader.MapAccessControl("o", "g", items).Mask.Should().BeNull();
    }

    [Fact]
    public void MapAccessControl_NamedEntriesButNoMask_CapsToNone_NotFailOpen()
    {
        // Anomalous (a real extended ACL always has a mask): named entry present, no mask entry.
        // Must not leave the named entry uncapped — cap to None (fail closed).
        var items = new List<PathAccessControlItem>
        {
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.User,  RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: "alice"),
            new(AccessControlType.Group, RolePermissions.Read | RolePermissions.Execute, defaultScope: false, entityId: null),
            new(AccessControlType.Other, RolePermissions.None, defaultScope: false, entityId: null),
        };

        Gen2Acl acl = DataLakeGen2DirectoryReader.MapAccessControl("o", "g", items);

        acl.Mask.Should().Be(Gen2Permission.None);
    }
}
