using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureSearchResultVerifierTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;
    private static readonly Guid User = Guid.NewGuid();
    private static readonly AzureIdentityRef Link = new("user-oid", "tid");

    private static SearchHit Hit(string docId, double score) =>
        new($"chunk-{docId}", docId, "content", (float)score, new Dictionary<string, string>());

    private static IOptionsMonitor<T> Opt<T>(T v) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(v);
        return m;
    }

    // Builds a verifier with all seams faked; each test overrides what it needs.
    private sealed class Harness
    {
        public IDocumentStore Docs = Substitute.For<IDocumentStore>();
        public IAzureIdentityLinkReader Links = Substitute.For<IAzureIdentityLinkReader>();
        public IAzureDirectoryReader Directory = Substitute.For<IAzureDirectoryReader>();
        public IAzureRbacReader Rbac = Substitute.For<IAzureRbacReader>();
        public IGen2FileAclReader FileAcl = Substitute.For<IGen2FileAclReader>();
        public IBlobTagReader Tags = Substitute.For<IBlobTagReader>();
        public IGen2DirectoryReader DirReader = Substitute.For<IGen2DirectoryReader>();
        public bool AzureConfigured = true;
        public bool AzureEnforcing = true;

        public Harness()
        {
            // Defaults are wired here (construction time) rather than in Build(), so that any
            // test-specific override set on these substitutes between `new Harness()` and
            // `Build()` is the LAST configuration registered for that call and therefore wins
            // (NSubstitute resolves repeat `.Returns()` on identical arguments by last-write).
            Links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
            Directory.ResolveAsync(Link, Arg.Any<CancellationToken>())
                .Returns(AzureIdentitySet.Resolved(["user-oid", "group-1"]));
            Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
                .Returns(AzureRbacScopes.Resolved([], []));
        }

        public AzureSearchResultVerifier Build()
        {
            var azureAd = Opt(AzureConfigured
                ? new AzureAdSignInSettings { TenantId = "t", ClientId = "c", RedirectUri = "https://x/cb", ClientCertificatePath = "p.pem" }
                : new AzureAdSignInSettings());
            var enf = Opt(new PermissionEnforcementSettings { AzureEnforcing = AzureEnforcing });
            var traverse = new AncestorTraverseResolver(DirReader, new MemoryCache(new MemoryCacheOptions()));
            return new AzureSearchResultVerifier(
                Docs, Links, Directory, Rbac, FileAcl, Tags, traverse,
                azureAd, enf, EnforcementMigration.Completed(),
                Options.Create(new AzureVerifierSettings { MaxParallelism = 16, CandidateMultiplier = 5 }),
                NullLogger<AzureSearchResultVerifier>.Instance);
        }
    }

    private void ResourceUris(Harness h, params (string doc, string? uri)[] map) =>
        h.Docs.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(map.ToDictionary(m => m.doc, m => m.uri));

    [Fact]
    public async Task NonAzureHits_PassUntouched()
    {
        var h = new Harness();
        ResourceUris(h, ("d1", "s3://b/k"), ("d2", null));
        var hits = new[] { Hit("d1", 0.9), Hit("d2", 0.8) };

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync(hits, User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("d1", "d2");
    }

    [Fact]
    public async Task AzureNotEnforcing_PassesEverything()
    {
        var h = new Harness { AzureEnforcing = false };
        ResourceUris(h, ("d1", "azblob://acct/docs/secret"));
        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);
        r.Should().ContainSingle();
    }

    [Fact]
    public async Task RbacCoveredHit_Passes_WithoutAnyLiveAclOrTagRead()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
            .Returns(AzureRbacScopes.Resolved([new AzureScope("azblob://acct/docs/")], []));
        ResourceUris(h, ("d1", "azblob://acct/docs/a/file.txt"));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().ContainSingle();
        await h.FileAcl.DidNotReceive().ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
        await h.Tags.DidNotReceive().ReadTagsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LockedFileBeneathReadableFolder_IsDropped()
    {
        // Soundness: no RBAC/tag; file's own ACL denies Read → drop, even if ancestors traverse.
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/locked.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, RX, Gen2Permission.None, null, [], [])); // other = no read
        h.DirReader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, RX, HasExtendedAcl: false)); // ancestors traverse

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().BeEmpty();
    }

    [Fact]
    public async Task CaseC_PerFileGrantBeneathUnreadableFolder_IsReturned()
    {
        // Completeness: file's own ACL grants Read to the user AND ancestors are traversable (X),
        // even though the folder is not readable as a listing (R). No RBAC. → pass.
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/secret/case-c.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [new Gen2NamedAce("user-oid", RX)], NamedGroups: []));
        h.DirReader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.Execute, HasExtendedAcl: false));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().ContainSingle();
    }

    [Fact]
    public async Task Gen2FileAclUnreadable_IsDropped_FailClosed()
    {
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/x.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2Acl?)null);

        (await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task TagConditioned_MatchingTag_Passes_And_NonMatching_Drops()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>()).Returns(
            AzureRbacScopes.Resolved([], [new AzureTagCondition("azblob://acct/docs/", "Project", "Cascade", true, false)]));
        ResourceUris(h, ("hit-ok", "azblob://acct/docs/a.txt"), ("hit-no", "azblob://acct/docs/b.txt"));
        h.Tags.ReadTagsAsync(Arg.Is<Gen2Path>(p => p.Path == "a.txt"), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Project"] = "Cascade" });
        h.Tags.ReadTagsAsync(Arg.Is<Gen2Path>(p => p.Path == "b.txt"), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Project"] = "Other" });

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("hit-ok", 0.9), Hit("hit-no", 0.8)], User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("hit-ok");
    }

    [Fact]
    public async Task DeprovisionedIdentity_DropsAllAzure_ButKeepsNonCloud()
    {
        var h = new Harness();
        h.Directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Deprovisioned());
        ResourceUris(h, ("az", "azblob://acct/docs/x"), ("nc", null));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("az", 0.9), Hit("nc", 0.8)], User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("nc");
    }

    [Fact]
    public async Task Backfill_KeepsTopKInRankOrder_AfterDroppingUnreadable()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
            .Returns(AzureRbacScopes.Resolved([new AzureScope("azblob://acct/ok/")], []));
        ResourceUris(h,
            ("d1", "azblob://acct/no/1"),  // dropped (not covered, file acl null)
            ("d2", "azblob://acct/ok/2"),  // pass
            ("d3", "azblob://acct/ok/3")); // pass
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2Acl?)null);

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync(
            [Hit("d1", 0.9), Hit("d2", 0.8), Hit("d3", 0.7)], User, 2);

        r.Select(x => x.DocumentId).Should().Equal("d2", "d3"); // rank order preserved, d1 backfilled out
    }

    [Fact]
    public async Task EnforcingButUnusable_DropsAllAzure_ButKeepsNonCloud()
    {
        // AzureEnforcing latched on but the provider isn't configured → EnforcingButUnusable, which
        // the sibling AzureSearchScopeResolver treats as SearchScopes.Failed ("searches deny"). The
        // verifier must not fall back to passing everything through.
        var h = new Harness { AzureConfigured = false, AzureEnforcing = true };
        ResourceUris(h, ("az", "azblob://acct/docs/x"), ("nc", null));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("az", 0.9), Hit("nc", 0.8)], User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("nc");
    }

    [Fact]
    public async Task UnparseableAzblobUri_IsDropped_FailClosed()
    {
        var h = new Harness();
        ResourceUris(h, ("bad", "azblob://acct/docs/")); // azblob scheme, empty blob path → unparseable

        (await h.Build().VerifyAsync([Hit("bad", 0.9)], User, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task DocumentIdNotFound_IsDropped_FailClosed()
    {
        var h = new Harness();
        ResourceUris(h); // no entries at all — "gone" is absent from the map, not merely null

        (await h.Build().VerifyAsync([Hit("gone", 0.9)], User, 10)).Should().BeEmpty();
    }
}
