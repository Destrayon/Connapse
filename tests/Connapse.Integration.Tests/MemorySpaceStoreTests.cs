using Connapse.Core;
using Connapse.Storage.Containers;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-235: a personal memory space exists only for its owner. The store never finds it for anyone else, by id, by name
/// or in a list, and a name taken by someone's personal space neither blocks nor reveals itself to anyone else.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpaceStoreTests : IAsyncLifetime
{
    /// <summary>The last migration before memory spaces.</summary>
    private const string BeforeSpaces = "20261103063347_AddMemoryScope";

    private static readonly MemoryCaller Alice = new(Guid.NewGuid(), null, "alice");
    private static readonly MemoryCaller Bob = new(Guid.NewGuid(), null, "bob");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("connapse_memory_spaces")
        .WithUsername("spaces_test")
        .WithPassword("spaces_test")
        .WithCreateParameterModifier(TestContainerLimits.Apply)
        .Build();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    private DbContextOptions<KnowledgeDbContext> Options() => new DbContextOptionsBuilder<KnowledgeDbContext>()
        .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector())
        .Options;

    private async Task<PostgresContainerStore> MigratedStoreAsync()
    {
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync();
        }
        return new PostgresContainerStore(new PooledDbContextFactory<KnowledgeDbContext>(Options()), NullLogger<PostgresContainerStore>.Instance);
    }

    [Fact]
    public async Task Migrate_ExistingContainers_BecomeTeam()
    {
        await using (var context = new KnowledgeDbContext(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync(BeforeSpaces);
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO containers (id, name, kind, created_at, updated_at) VALUES ({0}, 'old-notes', 'memory', now(), now())",
                Guid.NewGuid());
        }

        var store = await MigratedStoreAsync();

        var upgraded = await store.GetByNameAsync("old-notes");
        upgraded.Should().NotBeNull("every memory container before CON-235 is a team space");
        upgraded!.Audience.Should().Be(MemoryScope.Team);
        upgraded.OwnerUserId.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_SomeoneElsesPersonalSpace_IsNotFound()
    {
        var store = await MigratedStoreAsync();
        var space = await store.EnsurePersonalSpaceAsync(Alice, "my-memory", null);
        Guid id = Guid.Parse(space.Id);

        (await store.GetAsync(id, Bob)).Should().BeNull();
        (await store.GetAsync(id)).Should().BeNull("a lookup that doesn't say who is asking fails closed");
        (await store.GetAsync(id, null)).Should().BeNull();
        (await store.GetAsync(id, new MemoryCaller(null, Alice.UserId, null))).Should().BeNull("an agent id that equals a user id is not that user");

        var own = await store.GetAsync(id, Alice);
        own.Should().NotBeNull();
        own!.Audience.Should().Be(MemoryScope.Personal);
        own.Kind.Should().Be(ContainerKind.Memory);
        own.OwnerUserId.Should().Be(Alice.UserId);
    }

    [Fact]
    public async Task FindByNameAsync_SomeoneElsesPersonalSpace_IsNotFound()
    {
        var store = await MigratedStoreAsync();
        await store.EnsurePersonalSpaceAsync(Alice, "my-memory", null);

        (await store.FindByNameAsync("my-memory", Bob)).Should().BeEmpty();
        (await store.GetByNameAsync("my-memory")).Should().BeNull();
        (await store.FindByNameAsync(" MY-MEMORY ", Alice)).Should().ContainSingle().Which.Name.Should().Be("my-memory");
    }

    [Fact]
    public async Task ListAsync_SomeoneElsesPersonalSpace_IsLeftOut()
    {
        var store = await MigratedStoreAsync();
        await store.CreateAsync(new CreateContainerRequest("team-notes", null, ContainerKind.Memory));
        await store.EnsurePersonalSpaceAsync(Alice, "alice-notes", null);

        (await store.ListAsync(0, 50, Bob)).Select(c => c.Name).Should().Equal("team-notes");
        (await store.ListAsync()).Select(c => c.Name).Should().Equal("team-notes");
        (await store.ListAsync(0, 50, Alice)).Select(c => c.Name).Should().Equal("alice-notes", "team-notes");
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_SameNameForTeamAndTwoOwners_EachKeepsTheirOwn()
    {
        var store = await MigratedStoreAsync();
        var team = await store.CreateAsync(new CreateContainerRequest("notes", null, ContainerKind.Memory));
        var alices = await store.EnsurePersonalSpaceAsync(Alice, "notes", "mine");
        var bobs = await store.EnsurePersonalSpaceAsync(Bob, "notes", null);

        new[] { team.Id, alices.Id, bobs.Id }.Should().OnlyHaveUniqueItems();
        (await store.FindByNameAsync("notes", Alice)).Select(c => c.Id).Should().Equal(team.Id, alices.Id);
        (await store.FindByNameAsync("notes", Bob)).Select(c => c.Id).Should().Equal(team.Id, bobs.Id);
        (await store.GetByNameAsync("notes"))!.Id.Should().Be(team.Id);

        var create = () => store.CreateAsync(new CreateContainerRequest("notes"));
        await create.Should().ThrowAsync<InvalidOperationException>("the team name is still taken among team containers");
    }

    [Fact]
    public async Task CreateAsync_NameOfSomeonesPersonalSpace_IsNotBlocked()
    {
        var store = await MigratedStoreAsync();
        await store.EnsurePersonalSpaceAsync(Alice, "plans", null);

        var team = await store.CreateAsync(new CreateContainerRequest("plans"));

        team.Audience.Should().Be(MemoryScope.Team);
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_CalledTwice_ReturnsTheSameSpace()
    {
        var store = await MigratedStoreAsync();

        var first = await store.EnsurePersonalSpaceAsync(Alice, "my-memory", "first");
        var again = await store.EnsurePersonalSpaceAsync(Alice, "my-memory", "second");

        again.Id.Should().Be(first.Id);
        again.Description.Should().Be("first");
        (await store.ListAsync(0, 50, Alice)).Should().ContainSingle();
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_ConcurrentFirstSaves_MakeOneSpace()
    {
        var store = await MigratedStoreAsync();

        var made = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.EnsurePersonalSpaceAsync(Alice, "my-memory", null)));

        made.Select(c => c.Id).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_AgentActingForNoUser_OwnsItAlone()
    {
        var store = await MigratedStoreAsync();
        Guid agent = Guid.NewGuid();
        var space = await store.EnsurePersonalSpaceAsync(new MemoryCaller(null, agent, "bot"), "bot-memory", null);
        Guid id = Guid.Parse(space.Id);

        space.OwnerAgentId.Should().Be(agent);
        space.OwnerUserId.Should().BeNull();
        (await store.GetAsync(id, new MemoryCaller(null, agent, null))).Should().NotBeNull();
        (await store.GetAsync(id, new MemoryCaller(Alice.UserId, agent, null))).Should().BeNull("the agent acting for a user reads as that user");
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_UserActingThroughAnAgent_OwnsItAsTheUser()
    {
        var store = await MigratedStoreAsync();
        var space = await store.EnsurePersonalSpaceAsync(new MemoryCaller(Alice.UserId, Guid.NewGuid(), "bot"), "my-memory", null);

        space.OwnerUserId.Should().Be(Alice.UserId);
        space.OwnerAgentId.Should().BeNull();
        (await store.GetAsync(Guid.Parse(space.Id), Alice)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("x")]
    [InlineData("Not Valid")]
    public async Task EnsurePersonalSpaceAsync_InvalidName_Throws(string name)
    {
        var store = await MigratedStoreAsync();

        var ensure = () => store.EnsurePersonalSpaceAsync(Alice, name, null);

        await ensure.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsurePersonalSpaceAsync_OwnerWithNoId_Throws()
    {
        var store = await MigratedStoreAsync();

        var ensure = () => store.EnsurePersonalSpaceAsync(new MemoryCaller(null, null, "anon"), "my-memory", null);

        await ensure.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateAsync_RenamePersonalSpaceToATeamName_IsAllowed()
    {
        var store = await MigratedStoreAsync();
        await store.CreateAsync(new CreateContainerRequest("shared"));
        var space = await store.EnsurePersonalSpaceAsync(Alice, "mine", null);
        await store.EnsurePersonalSpaceAsync(Alice, "other", null);

        var renamed = await store.UpdateAsync(Guid.Parse(space.Id), "shared", null);

        renamed.Should().NotBeNull("rename answers the space it changed, though no viewer was named");
        renamed!.Name.Should().Be("shared");
        renamed.Audience.Should().Be(MemoryScope.Personal);
        var clash = () => store.UpdateAsync(Guid.Parse(space.Id), "other", null);
        await clash.Should().ThrowAsync<InvalidOperationException>("the owner already has a space with that name");
    }

    [Fact]
    public async Task Database_PersonalDocumentsContainer_IsRefused()
    {
        await MigratedStoreAsync();
        await using var context = new KnowledgeDbContext(Options());

        var insert = () => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, kind, audience, owner_user_id, created_at, updated_at) VALUES ({0}, 'docs', 'documents', 'personal', {1}, now(), now())",
            Guid.NewGuid(), Alice.UserId!);

        (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_containers_audience_owner");
    }
}
