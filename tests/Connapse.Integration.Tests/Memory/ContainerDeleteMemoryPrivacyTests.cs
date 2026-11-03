using System.Net;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Principal = Connapse.Integration.Tests.CrossSurface.Principal;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-231: deleting a container deletes its memory facts (they cascade with it), so a delete must never take facts
/// the deleter can't see. A container holding one is refused and every fact stays; one holding only facts the deleter
/// can see is deleted with them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerDeleteMemoryPrivacyTests(SharedWebAppFixture fixture) : IDisposable
{
    private readonly IServiceScope _scope = fixture.Factory.Services.CreateScope();

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task Delete_ContainerHoldingAnotherUsersPrivateFacts_IsRefusedWithTheirCountAndKeepsEveryFact()
    {
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        Guid other = await Principals.UserIdAsync(fixture, Principal.Viewer);
        Guid container = await ContainerWithFactsAsync(admin, other, other);

        try
        {
            var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");

            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
            body.Should().Contain("holds 2 memory facts you can't see").And.Contain("Their owners must forget them first");
            await using var db = await DbAsync();
            (await db.Containers.AnyAsync(c => c.Id == container)).Should().BeTrue();
            (await db.MemoryFacts.CountAsync(f => f.ContainerId == container)).Should().Be(3, "no fact goes, not even the deleter's own");
        }
        finally
        {
            await using var db = await DbAsync();
            await db.Containers.Where(c => c.Id == container).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Delete_ContainerHoldingOnlyTheDeletersOwnFacts_DeletesItAndThem()
    {
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        Guid container = await ContainerWithFactsAsync(admin, admin);

        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        await using var db = await DbAsync();
        (await db.Containers.AnyAsync(c => c.Id == container)).Should().BeFalse();
        (await db.MemoryFacts.CountAsync(f => f.ContainerId == container)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_ContainerHoldingFactsNobodyCanSeeAnyMore_DeletesItAndThem()
    {
        // A sourceless fact whose author's account is gone, and one citing a document that no longer exists: nobody
        // could ever forget either, so they must not keep the container forever.
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        Guid container = await ContainerWithFactsAsync(admin, Guid.NewGuid());
        await using (var db = await DbAsync())
        {
            Guid id = Guid.NewGuid();
            db.MemoryFacts.Add(new MemoryFactEntity
            {
                Id = id,
                ChainId = id,
                ContainerId = container,
                Subject = "gone",
                Attribute = "source",
                Value = "cites a deleted document",
                RecordedAt = DateTimeOffset.UtcNow,
                AuthorUserId = Guid.NewGuid(),
                Sources = [new MemoryFactSourceEntity { FactId = id, Ordinal = 0, Kind = 0, Ref = Guid.NewGuid().ToString() }],
            });
            await db.SaveChangesAsync();
        }

        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{container}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        await using var check = await DbAsync();
        (await check.Containers.AnyAsync(c => c.Id == container)).Should().BeFalse();
        (await check.MemoryFacts.CountAsync(f => f.ContainerId == container)).Should().Be(0);
    }

    [Fact]
    public async Task StoreDelete_ContainerStillHoldingFacts_IsRefused()
    {
        // The last guard: a fact written after the delete cleared memory must stop the delete, not cascade away.
        Guid container = await ContainerWithFactsAsync(Guid.NewGuid());
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IContainerStore>();

        try
        {
            Func<Task> delete = () => store.DeleteAsync(container);

            (await delete.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("memory facts");
            await using var db = await DbAsync();
            (await db.MemoryFacts.CountAsync(f => f.ContainerId == container)).Should().Be(1);
        }
        finally
        {
            await using var db = await DbAsync();
            await db.Containers.Where(c => c.Id == container).ExecuteDeleteAsync();
        }
    }

    /// <summary>A memory container with one sourceless fact by each author: each is seen only by the user who wrote it.</summary>
    private async Task<Guid> ContainerWithFactsAsync(params Guid[] authors)
    {
        Guid container = Guid.NewGuid();
        await using var db = await DbAsync();
        db.Containers.Add(new ContainerEntity { Id = container, Name = $"mem-del-{Guid.NewGuid():N}"[..30], Kind = ContainerKind.Memory });
        db.MemoryFacts.AddRange(authors.Select(author =>
        {
            Guid id = Guid.NewGuid();
            return new MemoryFactEntity
            {
                Id = id,
                ChainId = id,
                ContainerId = container,
                Subject = "private",
                Attribute = "note",
                Value = $"written by {author}",
                RecordedAt = DateTimeOffset.UtcNow,
                AuthorUserId = author,
            };
        }));
        await db.SaveChangesAsync();
        return container;
    }

    private Task<KnowledgeDbContext> DbAsync() =>
        _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
}
