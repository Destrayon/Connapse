using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class InviteServiceTests
{
    private static ConnapseIdentityDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ConnapseIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static UserManager<ConnapseUser> CreateMockUserManager()
    {
        var store = Substitute.For<IUserStore<ConnapseUser>>();
        var mgr = Substitute.For<UserManager<ConnapseUser>>(
            store, null, null, null, null, null, null, null, null);
        return mgr;
    }

    private static InviteService CreateService(
        ConnapseIdentityDbContext db,
        UserManager<ConnapseUser>? userManager = null) =>
        new(db, userManager ?? CreateMockUserManager(), NullLogger<InviteService>.Instance);

    // ── CreateInviteAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateInviteAsync_ValidViewerRole_ReturnsTokenAndInvitation()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var (token, invitation) = await sut.CreateInviteAsync("new@test.com", "Viewer", Guid.NewGuid());

        token.Should().NotBeNullOrWhiteSpace();
        invitation.Email.Should().Be("new@test.com");
        invitation.Role.Should().Be("Viewer");
    }

    [Fact]
    public async Task CreateInviteAsync_UserAlreadyExists_ThrowsInvalidOperation()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync("existing@test.com").Returns(new ConnapseUser { Email = "existing@test.com" });
        var sut = CreateService(db, mgr);

        var act = () => sut.CreateInviteAsync("existing@test.com", "Viewer", Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateInviteAsync_InvalidRole_ThrowsArgumentException()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var act = () => sut.CreateInviteAsync("new@test.com", "SuperAdmin", Guid.NewGuid());

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Invalid role*");
    }

    [Fact]
    public async Task CreateInviteAsync_OwnerRole_ThrowsInvalidOperation()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var act = () => sut.CreateInviteAsync("new@test.com", "Owner", Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Owner role cannot be assigned*");
    }

    [Fact]
    public async Task CreateInviteAsync_DuplicatePendingInvite_ThrowsInvalidOperation()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        await sut.CreateInviteAsync("dup@test.com", "Viewer", Guid.NewGuid());

        var act = () => sut.CreateInviteAsync("dup@test.com", "Editor", Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending invitation*");
    }

    // ── ValidateInviteAsync ───────────────────────────────────────────────

    [Fact]
    public async Task ValidateInviteAsync_ValidToken_ReturnsInvitation()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var (token, _) = await sut.CreateInviteAsync("v@test.com", "Viewer", Guid.NewGuid());

        var result = await sut.ValidateInviteAsync(token);

        result.Should().NotBeNull();
        result!.Email.Should().Be("v@test.com");
    }

    [Fact]
    public async Task ValidateInviteAsync_InvalidToken_ReturnsNull()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.ValidateInviteAsync("nonexistent-token");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateInviteAsync_ExpiredInvite_ReturnsNull()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var (token, _) = await sut.CreateInviteAsync("exp@test.com", "Viewer", Guid.NewGuid());

        // Manually expire
        var entity = await db.UserInvitations.SingleAsync();
        entity.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        var result = await sut.ValidateInviteAsync(token);

        result.Should().BeNull();
    }

    // ── RevokeInviteAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RevokeInviteAsync_PendingInvite_ReturnsTrueAndRemoves()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var (_, invitation) = await sut.CreateInviteAsync("rev@test.com", "Viewer", Guid.NewGuid());

        var result = await sut.RevokeInviteAsync(invitation.Id);

        result.Should().BeTrue();
        (await db.UserInvitations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RevokeInviteAsync_NonexistentId_ReturnsFalse()
    {
        using var db = CreateDbContext();
        var sut = CreateService(db);

        var result = await sut.RevokeInviteAsync(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeInviteAsync_AlreadyAccepted_ReturnsFalse()
    {
        using var db = CreateDbContext();
        var mgr = CreateMockUserManager();
        mgr.FindByEmailAsync(Arg.Any<string>()).Returns((ConnapseUser?)null);
        var sut = CreateService(db, mgr);

        var (_, invitation) = await sut.CreateInviteAsync("acc@test.com", "Viewer", Guid.NewGuid());

        // Mark as accepted
        invitation.AcceptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var result = await sut.RevokeInviteAsync(invitation.Id);

        result.Should().BeFalse();
    }
}
