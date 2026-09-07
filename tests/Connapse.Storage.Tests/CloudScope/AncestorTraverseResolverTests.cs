using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AncestorTraverseResolverTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;
    private const string Me = "me";
    private static IReadOnlySet<string> Groups(params string[] g) => new HashSet<string>(g);
    private static readonly IReadOnlySet<string> NoGroups = new HashSet<string>();

    private static Gen2Path File(string path) => new("acct", "fs", path);

    // Mode bits where "other" carries X (so anyone traverses), no extended ACL.
    private static Gen2ModeBits OpenDir() =>
        new("owner", "group", RX, RX, RX, HasExtendedAcl: false);

    // Mode bits where nobody but owner/group traverses (other has no X), no extended ACL.
    private static Gen2ModeBits ClosedDir() =>
        new("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: false);

    private static AncestorTraverseResolver Build(IGen2DirectoryReader reader) =>
        new(reader, new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task AllAncestorsGrantX_IsReadable()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns(OpenDir());

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me, NoGroups);

        ok.Should().BeTrue();
        // root, "a", "a/b" — three ancestor directories, file excluded.
        await reader.Received(3).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LosingXOnOneAncestor_IsNotReadable()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Is<Gen2Path>(p => p.Path == "a"), Arg.Any<CancellationToken>()).Returns(ClosedDir());
        reader.ReadModeBitsAsync(Arg.Is<Gen2Path>(p => p.Path != "a"), Arg.Any<CancellationToken>()).Returns(OpenDir());

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me, NoGroups);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ExtendedAcl_AndNotOwner_ReadsFullAcl_ForTheTieBreak()
    {
        // Mode bits say "other" has no X and the ACL is extended → a named entry could grant X, so
        // the full ACL is consulted; a named GROUP the requester belongs to grants X.
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: true));
        reader.ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [], NamedGroups: [new Gen2NamedAce("devs", RX)]));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), "someone", Groups("devs"));

        ok.Should().BeTrue();
        await reader.Received(1).ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Owner_ShortCircuits_WithoutReadingFullAcl_EvenWhenExtended()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("me", "group", RX, Gen2Permission.None, Gen2Permission.None, HasExtendedAcl: true));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, NoGroups);

        ok.Should().BeTrue();
        await reader.DidNotReceive().ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnreadableAncestor_FailsClosed_AndIsNotCached()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2ModeBits?)null);

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, NoGroups)).Should().BeFalse();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, NoGroups)).Should().BeFalse();

        // Not cached → read attempted again on the second call (root only, one dir).
        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfidentDecision_IsCached_SecondCallDoesNotReRead()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns(OpenDir());

        var resolver = Build(reader);
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me, NoGroups); // root + "a" = 2 reads
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me, NoGroups); // served from cache

        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentPrincipalSet_IsNotServedFromAnotherSetsCache()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        // "other" has no X and not extended → decision depends purely on owning-group membership.
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: false));

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), "in-group", Groups("group"))).Should().BeTrue();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), "stranger", NoGroups)).Should().BeFalse();
    }

    [Fact]
    public async Task GroupMembership_DoesNotSatisfyANamedUserEntry_ViaTheResolver()
    {
        // End-to-end guard for the cross-class over-grant: a named-USER entry whose oid is a group the
        // requester belongs to must not grant traverse (ADLS would grant nobody). "other" is None.
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "owning-group", RX, Gen2Permission.None, Gen2Permission.None, HasExtendedAcl: true));
        reader.ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "owning-group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [new Gen2NamedAce("grp-oid", RX)], NamedGroups: []));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), "member", Groups("grp-oid"));

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns<Gen2ModeBits?>(_ => throw new OperationCanceledException());

        Func<Task> act = () => Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, NoGroups, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
