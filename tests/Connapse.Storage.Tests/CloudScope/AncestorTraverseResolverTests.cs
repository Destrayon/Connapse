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
    private static readonly IReadOnlySet<string> Me = new HashSet<string> { "me" };

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

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me);

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

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("a/b/file.txt"), Me);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ExtendedAcl_AndNotOwner_ReadsFullAcl_ForTheTieBreak()
    {
        // Mode bits say "other" has no X and the ACL is extended → a named entry could grant X, so
        // the full ACL is consulted; a named group grants X.
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: true));
        reader.ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [], NamedGroups: [new Gen2NamedAce("me", RX)]));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me);

        ok.Should().BeTrue();
        await reader.Received(1).ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Owner_ShortCircuits_WithoutReadingFullAcl_EvenWhenExtended()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("me", "group", RX, Gen2Permission.None, Gen2Permission.None, HasExtendedAcl: true));

        bool ok = await Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me);

        ok.Should().BeTrue();
        await reader.DidNotReceive().ReadAccessAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnreadableAncestor_FailsClosed_AndIsNotCached()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2ModeBits?)null);

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me)).Should().BeFalse();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me)).Should().BeFalse();

        // Not cached → read attempted again on the second call (root only, one dir).
        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfidentDecision_IsCached_SecondCallDoesNotReRead()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns(OpenDir());

        var resolver = Build(reader);
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me); // root + "a" = 2 reads
        await resolver.HoldsTraverseOnAllAncestorsAsync(File("a/file.txt"), Me); // served from cache

        await reader.Received(2).ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DifferentPrincipalSet_IsNotServedFromAnotherSetsCache()
    {
        var reader = Substitute.For<IGen2DirectoryReader>();
        // "other" has no X and not extended → decision depends purely on group membership.
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.None, HasExtendedAcl: false));

        var resolver = Build(reader);
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), new HashSet<string> { "group" })).Should().BeTrue();
        (await resolver.HoldsTraverseOnAllAncestorsAsync(File("file.txt"), new HashSet<string> { "stranger" })).Should().BeFalse();
    }

    [Fact]
    public async Task CallerCancellation_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = Substitute.For<IGen2DirectoryReader>();
        reader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns<Gen2ModeBits?>(_ => throw new OperationCanceledException());

        Func<Task> act = () => Build(reader).HoldsTraverseOnAllAncestorsAsync(File("file.txt"), Me, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
