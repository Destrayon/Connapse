using System.Security.Claims;
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AuditLoggerTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (AuditLogger Logger, IHttpContextAccessor Accessor) CreateService(
        ConnapseIdentityDbContext db, ClaimsPrincipal? user = null, string? ipAddress = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();

        if (user is not null || ipAddress is not null)
        {
            var httpContext = new DefaultHttpContext();
            if (user is not null)
                httpContext.User = user;
            accessor.HttpContext.Returns(httpContext);
        }

        var logger = new AuditLogger(db, accessor, NullLogger<AuditLogger>.Instance);
        return (logger, accessor);
    }

    private static ClaimsPrincipal CreateUserPrincipal(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())]));

    // ── LogAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task LogAsync_BasicAction_PersistsEntryWithAction()
    {
        using var db = CreateDbContext();
        var (sut, _) = CreateService(db);

        await sut.LogAsync("user.login");

        var entry = await db.AuditLogs.SingleAsync();
        entry.Action.Should().Be("user.login");
    }

    [Fact]
    public async Task LogAsync_WithAuthenticatedUser_StoresUserId()
    {
        using var db = CreateDbContext();
        var userId = Guid.NewGuid();
        var (sut, _) = CreateService(db, CreateUserPrincipal(userId));

        await sut.LogAsync("doc.create");

        var entry = await db.AuditLogs.SingleAsync();
        entry.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task LogAsync_NoHttpContext_StoresNullUserId()
    {
        using var db = CreateDbContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        var sut = new AuditLogger(db, accessor, NullLogger<AuditLogger>.Instance);

        await sut.LogAsync("system.startup");

        var entry = await db.AuditLogs.SingleAsync();
        entry.UserId.Should().BeNull();
    }

    [Fact]
    public async Task LogAsync_WithResourceInfo_StoresResourceTypeAndId()
    {
        using var db = CreateDbContext();
        var (sut, _) = CreateService(db);

        await sut.LogAsync("doc.delete", resourceType: "Document", resourceId: "abc-123");

        var entry = await db.AuditLogs.SingleAsync();
        entry.ResourceType.Should().Be("Document");
        entry.ResourceId.Should().Be("abc-123");
    }

    [Fact]
    public async Task LogAsync_WithDetails_SerializesObjectToJson()
    {
        using var db = CreateDbContext();
        var (sut, _) = CreateService(db);

        await sut.LogAsync("settings.update", details: new { Key = "theme", Value = "dark" });

        var entry = await db.AuditLogs.SingleAsync();
        entry.Details.Should().NotBeNull();
        entry.Details!.RootElement.GetProperty("Key").GetString().Should().Be("theme");
    }

    [Fact]
    public async Task LogAsync_NullDetails_StoresNullDetailsColumn()
    {
        using var db = CreateDbContext();
        var (sut, _) = CreateService(db);

        await sut.LogAsync("user.logout");

        var entry = await db.AuditLogs.SingleAsync();
        entry.Details.Should().BeNull();
    }
}
