using Connapse.Application.Ports;
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>The user operations' account store, against real Identity (CON-14).</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UserDirectoryTests(SharedWebAppFixture fixture)
{
    private async Task<Guid> ViewerAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        string email = $"directory-{Guid.NewGuid():N}@integration-tests.connapse.io";
        var user = new ConnapseUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = email, CreatedAt = DateTime.UtcNow };
        (await users.CreateAsync(user, "DirectoryTest1!")).Succeeded.Should().BeTrue();
        (await users.AddToRoleAsync(user, "Viewer")).Succeeded.Should().BeTrue();
        return user.Id;
    }

    private async Task<IReadOnlyList<string>> RolesAsync(Guid id)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ConnapseUser>>();
        return (await users.GetRolesAsync((await users.FindByIdAsync(id.ToString()))!)).ToList();
    }

    [Fact]
    public async Task SetRoles_TheNewRolesCantBeAdded_KeepsTheOldOnes()
    {
        Guid id = await ViewerAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        // A role Identity doesn't have: the add fails after the old roles were removed.
        (await directory.SetRolesAsync(id, ["Viewer"], ["NoSuchRole"])).Outcome.Should().Be(DirectoryWrite.Failed);

        (await RolesAsync(id)).Should().BeEquivalentTo(["Viewer"], "a failed change must not leave the account without its roles");
    }

    [Fact]
    public async Task SetRoles_RolesChangedSinceTheCheck_WritesNothing()
    {
        Guid id = await ViewerAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        // The caller's rights were checked while the account was an Admin; it's a Viewer now.
        var change = await directory.SetRolesAsync(id, ["Admin"], ["Editor"]);

        change.Outcome.Should().Be(DirectoryWrite.Changed);
        (await RolesAsync(id)).Should().BeEquivalentTo(["Viewer"]);
    }

    [Fact]
    public async Task Delete_RolesChangedSinceTheCheck_DeletesNothing()
    {
        Guid id = await ViewerAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IUserDirectory>();

        (await directory.DeleteAsync(id, ["Editor"])).Should().Be(DirectoryWrite.Changed);
        (await directory.GetAsync(id)).Should().NotBeNull();
    }
}
