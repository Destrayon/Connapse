using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class PosixAclEvaluatorTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;

    private static Gen2Acl Acl(
        string? owner = "owner", string? group = "group",
        Gen2Permission ownerPerms = RX, Gen2Permission groupPerms = RX, Gen2Permission other = Gen2Permission.None,
        Gen2Permission? mask = null,
        IReadOnlyList<Gen2NamedAce>? namedUsers = null, IReadOnlyList<Gen2NamedAce>? namedGroups = null) =>
        new(owner, group, ownerPerms, groupPerms, other, mask, namedUsers ?? [], namedGroups ?? []);

    private static IReadOnlySet<string> Groups(params string[] oids) => new HashSet<string>(oids);
    private static readonly IReadOnlySet<string> NoGroups = new HashSet<string>();

    [Fact]
    public void Owner_Decides_AndIgnoresMask()
    {
        // Owner has X; a restrictive mask must NOT cut the owner down.
        var acl = Acl(ownerPerms: RX, mask: Gen2Permission.None);
        PosixAclEvaluator.Grants(acl, "owner", NoGroups, Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void Owner_WithoutTheBit_IsDenied_NotFallingThroughToGroup()
    {
        // Requester owns the dir (owner has no X) AND is in the owning group (group has X). Owner is
        // terminal: no X for the owner means denied, group never consulted.
        var acl = Acl(ownerPerms: Gen2Permission.Read, groupPerms: RX);
        PosixAclEvaluator.Grants(acl, "owner", Groups("group"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void NamedUser_Matched_IsCappedByMask()
    {
        var acl = Acl(other: RX,
            mask: Gen2Permission.Read, // mask strips X from named entries
            namedUsers: [new Gen2NamedAce("alice", RX)]);
        // Named-user match is terminal and masked → no X, even though "other" would have granted it.
        PosixAclEvaluator.Grants(acl, "alice", NoGroups, Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void NamedUser_Matched_Grants_WhenMaskAllows()
    {
        var acl = Acl(mask: RX, namedUsers: [new Gen2NamedAce("alice", RX)]);
        PosixAclEvaluator.Grants(acl, "alice", NoGroups, Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void OwningGroup_Grants_WhenMaskAllows()
    {
        var acl = Acl(owner: "someone-else", groupPerms: RX, mask: RX);
        PosixAclEvaluator.Grants(acl, "not-owner", Groups("group"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void AnyNamedGroup_Granting_Suffices()
    {
        // Two group matches: one denies X, one grants it. Any one granting → allow.
        var acl = Acl(owner: "someone-else", group: "not-mine",
            mask: RX,
            namedGroups: [new Gen2NamedAce("g1", Gen2Permission.Read), new Gen2NamedAce("g2", RX)]);
        PosixAclEvaluator.Grants(acl, "user", Groups("g1", "g2"), Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void GroupMatch_ButNoneGrant_IsDenied_NotFallingThroughToOther()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: RX,
            mask: RX, namedGroups: [new Gen2NamedAce("g1", Gen2Permission.Read)]);
        PosixAclEvaluator.Grants(acl, "user", Groups("g1"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void GroupClass_CappedByMask()
    {
        var acl = Acl(owner: "someone-else", group: "g", groupPerms: RX, mask: Gen2Permission.Read);
        PosixAclEvaluator.Grants(acl, "not-owner", Groups("g"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void Other_Decides_WhenNoOwnerNamedOrGroupMatch()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: RX);
        PosixAclEvaluator.Grants(acl, "stranger", NoGroups, Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void Other_WithoutTheBit_IsDenied()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: Gen2Permission.Read);
        PosixAclEvaluator.Grants(acl, "stranger", NoGroups, Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void Other_IgnoresMask()
    {
        // "other" is never capped by the mask. A stranger (no owner/named/group match) with X in
        // "other" is granted even when a restrictive mask is present.
        var acl = Acl(owner: "someone-else", group: "not-mine",
            other: Gen2Permission.Read | Gen2Permission.Execute, mask: Gen2Permission.None);
        PosixAclEvaluator.Grants(acl, "stranger", NoGroups, Gen2Permission.Execute).Should().BeTrue();
    }

    [Fact]
    public void Owner_TakesPrecedence_OverANamedUserEntryForTheSamePrincipal()
    {
        // Same principal is both owner and a named user; owner class wins (terminal, unmasked).
        var acl = Acl(ownerPerms: RX, mask: Gen2Permission.None,
            namedUsers: [new Gen2NamedAce("owner", Gen2Permission.None)]);
        PosixAclEvaluator.Grants(acl, "owner", NoGroups, Gen2Permission.Execute).Should().BeTrue();
    }

    // ---- Adversarial: user classes must NEVER be satisfied by group membership, and group classes
    // must NEVER be satisfied by the requester's own user oid. This is the over-grant the untyped
    // combined principal set allowed.

    [Fact]
    public void NamedUserEntry_WhoseOidIsAGroupOid_DoesNotGrantViaGroupMembership()
    {
        // An anomalous ACE: a user-typed entry whose id is actually a group's oid. ADLS evaluates
        // named-user entries by requester identity, so this grants nobody. It must NOT match a member
        // of that group here (which would be a cross-class over-grant).
        var acl = Acl(owner: "someone-else", group: "not-mine", other: Gen2Permission.None,
            mask: RX, namedUsers: [new Gen2NamedAce("grp-oid", RX)]);
        PosixAclEvaluator.Grants(acl, "member", Groups("grp-oid"), Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void OwningGroup_IsNotSatisfiedByTheRequestersUserOid()
    {
        // The requester's own user oid equals the owning-group oid (anomalous). The owning-group
        // class must match only via group membership, not the user oid → deny (other is None).
        var acl = Acl(owner: "someone-else", group: "g", groupPerms: RX, other: Gen2Permission.None, mask: RX);
        PosixAclEvaluator.Grants(acl, "g", NoGroups, Gen2Permission.Execute).Should().BeFalse();
    }

    [Fact]
    public void NamedGroup_IsNotSatisfiedByTheRequestersUserOid()
    {
        var acl = Acl(owner: "someone-else", group: "not-mine", other: Gen2Permission.None,
            mask: RX, namedGroups: [new Gen2NamedAce("devs", RX)]);
        PosixAclEvaluator.Grants(acl, "devs", NoGroups, Gen2Permission.Execute).Should().BeFalse();
    }
}
