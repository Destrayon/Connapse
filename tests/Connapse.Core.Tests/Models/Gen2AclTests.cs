using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class Gen2AclTests
{
    [Theory]
    [InlineData('r', '-', 'x', Gen2Permission.Read | Gen2Permission.Execute)]
    [InlineData('r', 'w', 'x', Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute)]
    [InlineData('-', '-', '-', Gen2Permission.None)]
    [InlineData('-', '-', 'x', Gen2Permission.Execute)]
    public void FromRwx_MapsEachBit(char r, char w, char x, Gen2Permission expected) =>
        Gen2Permissions.FromRwx(r, w, x).Should().Be(expected);

    [Fact]
    public void ParseSymbolic_NineChars_SplitsOwnerGroupOther()
    {
        var (owner, group, other, extended) = Gen2Permissions.ParseSymbolic("rwxr-x---");
        owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
        group.Should().Be(Gen2Permission.Read | Gen2Permission.Execute);
        other.Should().Be(Gen2Permission.None);
        extended.Should().BeFalse();
    }

    [Fact]
    public void ParseSymbolic_TrailingPlus_MarksExtendedAcl()
    {
        var (_, _, _, extended) = Gen2Permissions.ParseSymbolic("rwxr-x---+");
        extended.Should().BeTrue();
    }

    [Fact]
    public void ParseSymbolic_TenCharLeadingType_IgnoresTypeChar()
    {
        // Some listings prefix a file-type char (e.g. 'd' for directory): "drwxr-x---".
        var (owner, _, _, _) = Gen2Permissions.ParseSymbolic("drwxr-x---");
        owner.Should().Be(Gen2Permission.Read | Gen2Permission.Write | Gen2Permission.Execute);
    }

    [Fact]
    public void ModeBits_ToAcl_CarriesOwnerGroupOther_NoNamedNoMask()
    {
        var bits = new Gen2ModeBits("owner-oid", "group-oid",
            Gen2Permission.Read | Gen2Permission.Execute, Gen2Permission.Execute, Gen2Permission.None,
            HasExtendedAcl: false);

        Gen2Acl acl = bits.ToAcl();

        acl.OwnerOid.Should().Be("owner-oid");
        acl.OwningGroupOid.Should().Be("group-oid");
        acl.OwnerPermissions.Should().Be(Gen2Permission.Read | Gen2Permission.Execute);
        acl.OwningGroupPermissions.Should().Be(Gen2Permission.Execute);
        acl.OtherPermissions.Should().Be(Gen2Permission.None);
        acl.Mask.Should().BeNull();
        acl.NamedUsers.Should().BeEmpty();
        acl.NamedGroups.Should().BeEmpty();
    }
}
