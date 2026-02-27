using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connapse.Identity.Tests;

[Trait("Category", "Unit")]
public class AdminSeedServiceTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ConnapseIdentityDbContext _db;

    public AdminSeedServiceTests()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ConnapseIdentityDbContext>(opts =>
            opts.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddIdentity<ConnapseUser, ConnapseRole>(opts =>
        {
            // Relax password rules for test users
            opts.Password.RequireDigit = false;
            opts.Password.RequireLowercase = false;
            opts.Password.RequireUppercase = false;
            opts.Password.RequireNonAlphanumeric = false;
            opts.Password.RequiredLength = 4;
            opts.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<ConnapseIdentityDbContext>()
        .AddDefaultTokenProviders();

        services.AddLogging();
        _provider = services.BuildServiceProvider();
        _db = _provider.GetRequiredService<ConnapseIdentityDbContext>();
    }

    public void Dispose() => _provider.Dispose();

    private AdminSeedService CreateService(
        string? adminEmail = null,
        string? adminPassword = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONNAPSE_ADMIN_EMAIL"] = adminEmail,
                ["CONNAPSE_ADMIN_PASSWORD"] = adminPassword,
            })
            .Build();

        return new AdminSeedService(
            _provider.GetRequiredService<UserManager<ConnapseUser>>(),
            _provider.GetRequiredService<RoleManager<ConnapseRole>>(),
            config,
            NullLogger<AdminSeedService>.Instance);
    }

    // ── Role seeding ─────────────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_EmptyDatabase_CreatesAllDefaultRoles()
    {
        var sut = CreateService();

        await sut.SeedAsync();

        var roleManager = _provider.GetRequiredService<RoleManager<ConnapseRole>>();
        foreach (var roleName in AdminSeedService.DefaultRoles)
        {
            (await roleManager.RoleExistsAsync(roleName))
                .Should().BeTrue(because: $"role '{roleName}' should be seeded");
        }
    }

    [Fact]
    public async Task SeedAsync_CalledTwice_DoesNotCreateDuplicateRoles()
    {
        var sut = CreateService();

        await sut.SeedAsync();
        await sut.SeedAsync();

        var roles = _db.Roles.ToList();
        roles.Should().HaveCount(AdminSeedService.DefaultRoles.Length);
    }

    // ── Admin user seeding ───────────────────────────────────────────────

    [Fact]
    public async Task SeedAsync_WithEnvVars_CreatesAdminWithOwnerAndAdminRoles()
    {
        var sut = CreateService("admin@example.com", "pass");

        await sut.SeedAsync();

        var userManager = _provider.GetRequiredService<UserManager<ConnapseUser>>();
        var user = await userManager.FindByEmailAsync("admin@example.com");
        user.Should().NotBeNull();
        (await userManager.IsInRoleAsync(user!, "Owner")).Should().BeTrue();
        (await userManager.IsInRoleAsync(user!, "Admin")).Should().BeTrue();
    }

    [Fact]
    public async Task SeedAsync_MissingEnvVars_DoesNotCreateAdminUser()
    {
        var sut = CreateService(adminEmail: null, adminPassword: null);

        await sut.SeedAsync();

        _db.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task SeedAsync_ExistingAdmin_DoesNotCreateDuplicate()
    {
        var sut = CreateService("admin@example.com", "pass");
        await sut.SeedAsync();

        await sut.SeedAsync(); // second seed

        var userManager = _provider.GetRequiredService<UserManager<ConnapseUser>>();
        var users = await userManager.GetUsersInRoleAsync("Admin");
        users.Should().HaveCount(1);
    }
}
