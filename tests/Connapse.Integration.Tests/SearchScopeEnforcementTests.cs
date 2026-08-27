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
/// That the permission predicate actually narrows a search, against the real SQL.
/// </summary>
/// <remarks>
/// The predicate is hand-written SQL in two places, and the failure that matters is silent: a
/// filter that does not filter returns exactly what an unfiltered search returns, and every
/// assertion about hit counts and ordering still passes. So these assert the negative — that a
/// document outside the scope is <i>absent</i> — which is the only shape of test that can fail
/// when the filter is missing.
/// <para>
/// Keyword rather than vector, because it needs no embedding provider and exercises the same
/// predicate against the same join. The vector side shares its construction; a test needing Ollama
/// would be one more reason for this to be skipped when it matters most.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SearchScopeEnforcementTests(SharedWebAppFixture fixture)
{
    private const string Term = "zarquon";

    private static KeywordSearchService Build(KnowledgeDbContext db) =>
        new(db, NullLogger<KeywordSearchService>.Instance);

    /// <summary>A context of this test's own, per the DbContextFactory convention.</summary>
    /// <remarks>
    /// Resolving the scoped <c>KnowledgeDbContext</c> directly works here and is the habit worth
    /// not forming: this application registers the factory precisely because a scoped context
    /// shared across threads is the Blazor Server failure it exists to avoid.
    /// </remarks>
    private static Task<KnowledgeDbContext> NewContextAsync(IServiceProvider sp) =>
        sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();

    /// <summary>Two documents in one container, in different buckets.</summary>
    private static async Task<Guid> SeedAsync(KnowledgeDbContext db)
    {
        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("mine.md", "s3://acme/team/mine.md"),
                     ("theirs.md", "s3://acme/other/theirs.md"),
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

    private static SearchOptions For(Guid containerId) =>
        new(TopK: 20, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword);

    [Fact]
    public async Task Search_WithScopesCoveringOneBucket_ExcludesTheOther()
    {
        // The assertion this whole phase exists for.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);
        var containerId = await SeedAsync(db);

        var hits = await Build(db).SearchAsync(
            Term, For(containerId), SearchScopes.Of(["s3://acme/team/"]));

        hits.Should().ContainSingle();
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("mine.md");
    }

    [Fact]
    public async Task Search_WithoutScopes_ReturnsBoth()
    {
        // The control. Without it the test above passes just as well when the seed is broken, the
        // term never matches, or the query returns nothing for an unrelated reason.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);
        var containerId = await SeedAsync(db);

        var hits = await Build(db).SearchAsync(
            Term, For(containerId), SearchScopes.Unrestricted);

        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_WithNoScopesAtAll_ReturnsNothing()
    {
        // A resolved user with no grants. Distinct from unrestricted, and the distinction is the
        // difference between a closed door and an open one.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);
        var containerId = await SeedAsync(db);

        var hits = await Build(db).SearchAsync(
            Term, For(containerId), SearchScopes.None);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ForADocumentWithNoResourceUri_ExcludesItWhenScoped()
    {
        // Null is denied, never allowed. An upload has no external address, so no permission rule
        // can be checked against it — and the tempting default, treating null as "not covered by
        // any restriction", would make every upload visible to everyone the moment filtering is
        // switched on.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = container.Id,
            FileName = "upload.md",
            Path = "/upload.md",
            ResourceUri = null,
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
        await db.SaveChangesAsync();

        var service = Build(db);

        (await service.SearchAsync(Term, For(container.Id), SearchScopes.Unrestricted))
            .Should().ContainSingle("the document is findable when nothing is filtering");

        (await service.SearchAsync(Term, For(container.Id), SearchScopes.Of(["s3://acme/"])))
            .Should().BeEmpty("a document with no location cannot be inside any scope");
    }

    [Fact]
    public async Task Search_WithAnUnderscoreInTheGrant_DoesNotMatchAnyOtherCharacter()
    {
        // Against the real SQL, because the escaping and the ESCAPE clause have to agree and only
        // Postgres can say whether they do. A unit test of the pattern string cannot catch a
        // missing ESCAPE clause, which is exactly the half that would leave this open.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("granted.md", "s3://acme/team_docs/granted.md"),
                     ("sneaky.md", "s3://acme/teamXdocs/sneaky.md"),
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

        var hits = await Build(db).SearchAsync(
            Term, For(container.Id), SearchScopes.Of(["s3://acme/team_docs/"]));

        hits.Should().ContainSingle("only the granted prefix is reachable");
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("granted.md");
    }

    [Fact]
    public async Task Search_WithABucketScopedGrant_DoesNotReachASimilarlyNamedBucket()
    {
        // Against real SQL, because this is the shape AWS actually returns for a whole-bucket
        // grant and the leak is invisible in a unit test of the pattern string.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("granted.md", "s3://acme/report.md"),
                     ("sneaky.md", "s3://acme-secrets/payroll.md"),
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

        var hits = await Build(db).SearchAsync(
            Term, For(container.Id), SearchScopes.Of([GrantScope.Parse("s3://acme*")]));

        hits.Should().ContainSingle("a grant for one bucket does not reach another whose name starts the same");
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("granted.md");
    }

    [Fact]
    public async Task Search_WithAnObjectScopedGrant_DoesNotReachASuffixedSibling()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await using var db = await NewContextAsync(scope.ServiceProvider);

        var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);

        foreach (var (name, uri) in new[]
                 {
                     ("granted.md", "s3://acme/reports/q3.pdf"),
                     ("sneaky.md", "s3://acme/reports/q3.pdf.bak"),
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

        var hits = await Build(db).SearchAsync(
            Term, For(container.Id),
            SearchScopes.Of([GrantScope.Parse("s3://acme/reports/q3.pdf", isObjectScope: true)]));

        hits.Should().ContainSingle("an object grant names one object");
        hits[0].Metadata.GetValueOrDefault("fileName").Should().Be("granted.md");
    }
}
