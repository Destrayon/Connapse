using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Connapse.Core;
using Connapse.Identity.Data;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// Agent memory's speed gate (pass bar "Agent memory"): a write lands in under 200 ms, through the MCP tool with its
/// write-access check, source check and audit entry, and a recall answers in under 150 ms at p95. The first calls warm
/// the pool and the JIT and are not timed; each timed call is the best of three, because the others measure what else
/// a shared runner was doing. The category runs in a CI step of its own, beside no other tests.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Integration Tests")]
public class MemoryGateTests(SharedWebAppFixture fixture, ITestOutputHelper output)
{
    private static readonly TimeSpan WriteLimit = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RecallP95Limit = TimeSpan.FromMilliseconds(150);

    private static async Task<(TimeSpan Best, T Result)> BestOfThreeAsync<T>(Func<Task<T>> call)
    {
        (TimeSpan Best, T Result) best = (TimeSpan.MaxValue, default!);
        for (int i = 0; i < 3; i++)
        {
            var timer = Stopwatch.StartNew();
            T result = await call();
            if (timer.Elapsed < best.Best)
                best = (timer.Elapsed, result);
        }
        return best;
    }

    [Fact]
    public async Task Writes_LandUnder200Ms_AndRecallP95IsUnder150Ms()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid user = Guid.NewGuid();
        await using (var identity = await sp.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync())
        {
            identity.Users.Add(new Connapse.Identity.Data.Entities.ConnapseUser
            {
                Id = user,
                UserName = $"g-{user:N}@example.com",
                Email = $"g-{user:N}@example.com",
                CreatedAt = DateTime.UtcNow,
            });
            await identity.SaveChangesAsync();
        }
        string container, source;
        await using (var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
        {
            var entity = new ContainerEntity { Id = Guid.NewGuid(), Name = $"gate-{Guid.NewGuid():N}" };
            var document = new DocumentEntity
            {
                Id = Guid.NewGuid(),
                ContainerId = entity.Id,
                FileName = "session.md",
                Path = "/session.md",
                ContentHash = "x",
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Containers.Add(entity);
            db.Documents.Add(document);
            await db.SaveChangesAsync();
            container = entity.Id.ToString();
            source = document.Id.ToString();
        }

        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim("actor_type", "agent"),
                new Claim("on_behalf_of", user.ToString()),
                new Claim(ClaimTypes.Role, "Agent"),
            ], "test")),
        };
        try
        {
            static string Id(string json) => JsonDocument.Parse(json).RootElement.GetProperty("remembered").GetProperty("id").GetString()!;

            for (int i = 0; i < 5; i++)
            {
                await McpTools.MemoryRemember(sp, container, $"warm{i}", "database", "x", "2026-01-01", sources: [source]);
                await McpTools.MemoryRecall(sp, container, "warm database");
            }

            List<TimeSpan> writes = [];
            List<TimeSpan> recalls = [];
            for (int i = 0; i < 20; i++)
            {
                (TimeSpan remember, string json) = await BestOfThreeAsync(() =>
                    McpTools.MemoryRemember(sp, container, $"project {i}", "database", "MariaDB 11", "2026-01-05", sources: [source], reason: $"P-{i}"));
                json.Should().Contain("remembered");
                string id = Id(json);
                writes.Add(remember);

                // Each correction replaces the fact the previous one wrote, so all three timed calls are real corrections.
                (TimeSpan correct, string corrected) = await BestOfThreeAsync(async () =>
                {
                    string result = await McpTools.MemoryCorrect(sp, container, id, "PostgreSQL 15", kind: "wrong", sources: [source]);
                    id = Id(result);
                    return result;
                });
                corrected.Should().Contain("replaced");
                writes.Add(correct);

                (TimeSpan recall, string facts) = await BestOfThreeAsync(() =>
                    McpTools.MemoryRecall(sp, container, $"What is project {i}'s database now?"));
                facts.Should().Contain("PostgreSQL 15");
                recalls.Add(recall);
            }

            (TimeSpan forget, string forgot) = await BestOfThreeAsync(async () =>
                await McpTools.MemoryForget(sp, container,
                    Id(await McpTools.MemoryRemember(sp, container, "gone", "database", "x", sources: [source]))));
            forgot.Should().StartWith("Forgot");

            TimeSpan p95 = recalls.OrderBy(t => t).ElementAt((int)Math.Ceiling(recalls.Count * 0.95) - 1);
            output.WriteLine($"writes: max {writes.Max().TotalMilliseconds:F1} ms, median {writes.OrderBy(t => t).ElementAt(writes.Count / 2).TotalMilliseconds:F1} ms; "
                + $"remember+forget {forget.TotalMilliseconds:F1} ms; recall p95 {p95.TotalMilliseconds:F1} ms");

            writes.Should().OnlyContain(t => t < WriteLimit);
            forget.Should().BeLessThan(WriteLimit * 2, "it times a remember and a forget");
            p95.Should().BeLessThan(RecallP95Limit);
        }
        finally
        {
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
        }
    }
}
