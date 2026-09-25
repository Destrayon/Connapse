using Connapse.Core;
using Connapse.Search.Keyword;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Private GitHub documents through a real search: with AWS and Azure not filtering, a
/// <c>github://</c> document is found only when the composite granted its repository, and a
/// repository's prefix never admits another whose id merely starts with the same digits.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GitHubPrivateDocumentEnforcementTests(SharedWebAppFixture fixture)
{
    private const string Term = "quuxwibble";
    private static readonly CompositeSearchScopeResolver.Combiner Rule = new();

    private static async Task<Guid> SeedAsync(KnowledgeDbContext db)
    {
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new (string, string?)[]
                 {
                     ("repo-100.md", "github://100/issues/1.md"),
                     ("repo-1000.md", "github://1000/issues/1.md"),
                     ("aws.md", "s3://bucket/x"),
                     ("public-or-upload.md", null),
                 })
        {
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(), ContainerId = container.Id, FileName = name, Path = "/" + name,
                ResourceUri = uri, ContentHash = Guid.NewGuid().ToString("N"), Status = "Ready",
                CreatedAt = DateTime.UtcNow, Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(), DocumentId = document.Id, OwnerId = container.Id,
                Content = $"the {Term} appears here", ChunkIndex = 0, Metadata = [],
            });
        }

        await db.SaveChangesAsync();
        return container.Id;
    }

    private async Task<IEnumerable<string?>> SearchAsync(SearchScopes github)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var containerId = await SeedAsync(db);

        // AWS and Azure are not filtering; only GitHub differs between the cases.
        var scopes = Rule.Combine(SearchScopes.Unrestricted, SearchScopes.Unrestricted, github);
        var hits = await new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance)
            .SearchAsync(Term, new SearchOptions(TopK: 20, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword), scopes);
        return hits.Select(h => h.Metadata.GetValueOrDefault("fileName"));
    }

    [Fact]
    public async Task GrantedRepository_IsFound_AndOnlyThatRepository() =>
        (await SearchAsync(SearchScopes.OfPrefixes([GitHubSearchScopeResolver.DocumentPrefix(100)])))
            .Should().BeEquivalentTo(["repo-100.md", "aws.md", "public-or-upload.md"],
                "github://100/ must not admit github://1000/");

    [Fact]
    public async Task NothingGranted_HidesEveryPrivateRepository() =>
        (await SearchAsync(SearchScopes.None)).Should().BeEquivalentTo(["aws.md", "public-or-upload.md"]);

    [Fact]
    public async Task GitHubResolverFailed_HidesEveryPrivateRepository() =>
        (await SearchAsync(SearchScopes.Failed)).Should().BeEquivalentTo(["aws.md", "public-or-upload.md"]);
}
