using Connapse.Core;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The flat end-to-end proof for Azure: the composite's per-scheme scopes narrow a real search the
/// same way the AWS-only <see cref="SearchScopeEnforcementTests"/> proves for S3, and a non-cloud
/// document survives regardless of which cloud is enforcing or failing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureFlatEnforcementTests(SharedWebAppFixture fixture)
{
    private const string Term = "zarquon";

    private static KeywordSearchService Build(KnowledgeDbContext db) =>
        new(db, NullLogger<KeywordSearchService>.Instance);

    private static Task<KnowledgeDbContext> NewContextAsync(IServiceProvider sp) =>
        sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();

    private static SearchOptions For(Guid containerId) =>
        new(TopK: 20, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword);

    /// <summary>Four documents in one container: an in-scope Azure doc, an out-of-scope Azure doc,
    /// an AWS doc, and a non-cloud doc (resource_uri NULL).</summary>
    private static async Task<Guid> SeedAsync(KnowledgeDbContext db)
    {
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new (string, string?)[]
                 {
                     ("in-scope.md", "azblob://acct/docs/a"),
                     ("out-of-scope.md", "azblob://acct/secret/b"),
                     ("aws.md", "s3://bucket/x"),
                     ("non-cloud.md", null),
                 })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                ContentHash = Guid.NewGuid().ToString("N"),
                Status = "Ready",
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);

            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = $"the {Term} appears here",
                ChunkIndex = 0,
                Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return container.Id;
    }

    [Fact]
    public async Task AzurePrefixPlusAwsWildcard_AdmitsInScopeAzure_AwsAndNonCloud_ExcludesOutOfScopeAzure()
    {
        // What the composite yields for an Azure-granted / AWS-unrestricted user: the Azure prefix
        // plus the AWS scheme wildcard.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);
        var containerId = await SeedAsync(db);

        var scopes = SearchScopes.Of(
        [
            new GrantMatch("azblob://acct/docs/", IsExact: false),
            new GrantMatch("s3://", IsExact: false),
        ]);

        var hits = await Build(db).SearchAsync(Term, For(containerId), scopes);

        hits.Select(h => h.Metadata.GetValueOrDefault("fileName")).Should().BeEquivalentTo(
            "in-scope.md", "aws.md", "non-cloud.md");
    }

    [Fact]
    public async Task AzureFailed_HidesAllAzure_ButKeepsNonCloud()
    {
        // Azure contributed nothing (denied/failed); only the AWS wildcard survives.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);
        var containerId = await SeedAsync(db);

        var scopes = SearchScopes.Of([new GrantMatch("s3://", IsExact: false)]);

        var hits = await Build(db).SearchAsync(Term, For(containerId), scopes);

        hits.Select(h => h.Metadata.GetValueOrDefault("fileName")).Should().BeEquivalentTo(
            "aws.md", "non-cloud.md");
    }
}
