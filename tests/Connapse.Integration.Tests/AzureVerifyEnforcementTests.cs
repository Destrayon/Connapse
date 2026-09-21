using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// End-to-end proof of the Phase 4e verify seam: <see cref="AzureSearchResultVerifier"/> built with
/// the real <see cref="IDocumentStore"/> from the shared fixture (so
/// <c>GetResourceUrisAsync</c> runs against the actual database) plus faked Azure identity/RBAC/ACL
/// readers, verifying seeded azblob/s3/non-cloud documents the same way
/// <see cref="AzureFlatEnforcementTests"/> proves the composite scope resolver end to end.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureVerifyEnforcementTests(SharedWebAppFixture fixture)
{
    private static readonly Guid TestUser = Guid.NewGuid();
    private static readonly AzureIdentityRef Link = new("user-oid", "tid");

    private static SearchHit Hit(Guid docId, double score) =>
        new($"chunk-{docId}", docId.ToString(), "content", (float)score, new Dictionary<string, string>());

    private static IOptionsMonitor<T> Opt<T>(T v) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(v);
        return m;
    }

    /// <summary>Four documents in one container: an azblob doc under an RBAC-readable prefix, an
    /// azblob doc with no grant, an AWS doc, and a non-cloud doc (resource_uri NULL).</summary>
    private static async Task<(Guid ContainerId, Guid InRbac, Guid NoGrant, Guid Aws, Guid NonCloud)> SeedAsync(
        KnowledgeDbContext db)
    {
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        Guid inRbac = Guid.NewGuid(), noGrant = Guid.NewGuid(), aws = Guid.NewGuid(), nonCloud = Guid.NewGuid();

        foreach (var (id, name, uri) in new (Guid, string, string?)[]
                 {
                     (inRbac, "in-rbac.md", "azblob://acct/docs/a"),
                     (noGrant, "no-grant.md", "azblob://acct/secret/b"),
                     (aws, "aws.md", "s3://bucket/x"),
                     (nonCloud, "non-cloud.md", null),
                 })
        {
            db.Documents.Add(new DocumentEntity
            {
                Id = id,
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return (container.Id, inRbac, noGrant, aws, nonCloud);
    }

    /// <summary>Builds the verifier against the real <paramref name="documents"/> store with every
    /// Azure identity/RBAC/ACL seam faked — live calls to Entra/ARM/ADLS/Blob can't run in CI, only
    /// the resource_uri lookup needs to be real.</summary>
    private static AzureSearchResultVerifier BuildVerifier(
        IDocumentStore documents, bool azureEnforcing, IReadOnlyList<AzureScope> readablePrefixes)
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(TestUser, Arg.Any<CancellationToken>()).Returns(Link);

        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>())
            .Returns(AzureIdentitySet.Resolved(["user-oid"]));

        var rbac = Substitute.For<IAzureRbacReader>();
        rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
            .Returns(AzureRbacScopes.Resolved(readablePrefixes, []));

        var fileAcl = Substitute.For<IGen2FileAclReader>();
        var blobTags = Substitute.For<IBlobTagReader>();
        var gen2DirReader = Substitute.For<IGen2DirectoryReader>();
        var traverse = new AncestorTraverseResolver(gen2DirReader, new MemoryCache(new MemoryCacheOptions()));

        var azureAd = Opt(new AzureAdSignInSettings
        {
            TenantId = "t",
            ClientId = "c",
            RedirectUri = "https://x/cb",
            ClientCertificatePath = "p.pem",
        });
        var enforcement = Opt(new PermissionEnforcementSettings { AzureEnforcing = azureEnforcing });

        return new AzureSearchResultVerifier(
            documents, links, directory, rbac, fileAcl, blobTags, traverse,
            azureAd, enforcement, EnforcementMigration.Completed(),
            Options.Create(new AzureVerifierSettings { MaxParallelism = 16, CandidateMultiplier = 5 }),
            NullLogger<AzureSearchResultVerifier>.Instance);
    }

    [Fact]
    public async Task Enforcing_AdmitsRbacCoveredAzblob_DropsUngranted_PassesAwsAndNonCloud()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var db = await factoryDb.CreateDbContextAsync();
        var (containerId, inRbac, noGrant, aws, nonCloud) = await SeedAsync(db);

        try
        {
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var verifier = BuildVerifier(documents, azureEnforcing: true, [new AzureScope("azblob://acct/docs/")]);

            SearchHit[] ranked =
            [
                Hit(inRbac, 0.9),
                Hit(noGrant, 0.85),
                Hit(aws, 0.8),
                Hit(nonCloud, 0.7),
            ];

            IReadOnlyList<SearchHit> result = await verifier.VerifyAsync(ranked, TestUser, topK: 10);

            result.Select(h => h.DocumentId).Should().BeEquivalentTo(
                inRbac.ToString(), aws.ToString(), nonCloud.ToString());
        }
        finally
        {
            await CleanupAsync(factoryDb, containerId);
        }
    }

    [Fact]
    public async Task NotEnforcing_PassesEverythingThrough()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factoryDb = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var db = await factoryDb.CreateDbContextAsync();
        var (containerId, inRbac, noGrant, aws, nonCloud) = await SeedAsync(db);

        try
        {
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            // No RBAC scopes at all — proves the pass-through is because enforcement is off, not
            // because a grant happens to cover everything.
            var verifier = BuildVerifier(documents, azureEnforcing: false, []);

            SearchHit[] ranked =
            [
                Hit(inRbac, 0.9),
                Hit(noGrant, 0.85),
                Hit(aws, 0.8),
                Hit(nonCloud, 0.7),
            ];

            IReadOnlyList<SearchHit> result = await verifier.VerifyAsync(ranked, TestUser, topK: 10);

            result.Select(h => h.DocumentId).Should().BeEquivalentTo(
                inRbac.ToString(), noGrant.ToString(), aws.ToString(), nonCloud.ToString());
        }
        finally
        {
            await CleanupAsync(factoryDb, containerId);
        }
    }

    private static async Task CleanupAsync(IDbContextFactory<KnowledgeDbContext> factoryDb, Guid containerId)
    {
        await using var ctx = await factoryDb.CreateDbContextAsync();
        var docs = await ctx.Documents.Where(d => d.ContainerId == containerId).ToListAsync();
        ctx.Documents.RemoveRange(docs);
        var container = await ctx.Containers.FirstOrDefaultAsync(c => c.Id == containerId);
        if (container is not null) ctx.Containers.Remove(container);
        await ctx.SaveChangesAsync();
    }
}
