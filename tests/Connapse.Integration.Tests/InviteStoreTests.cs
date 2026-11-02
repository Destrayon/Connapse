using Connapse.Application.Ports;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>The invite operations' store, against real Identity and PostgreSQL (CON-70).</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class InviteStoreTests(SharedWebAppFixture fixture)
{
    private static string NewEmail() => $"store-{Guid.NewGuid():N}@integration-tests.connapse.io";

    [Fact]
    public async Task Create_ManyAtOnceForOneAddress_MakesOneInviteAndAnswersOneToken()
    {
        string email = NewEmail();
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IInviteStore>().CreateAsync(email, "Viewer", admin);
        }));

        outcomes.Count(o => o.Outcome == InviteWrite.Done).Should().Be(1, "one address holds one pending invite, so one link");
        outcomes.Where(o => o.Outcome != InviteWrite.Done).Should().OnlyContain(o => o.Outcome == InviteWrite.PendingExists && o.Token == null);
        using var check = fixture.Factory.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>().UserInvitations.CountAsync(i => i.Email == email)).Should().Be(1);
    }

    [Fact]
    public async Task Create_ThePolicyRefusesAtTheWrite_AnswersForbiddenNotPending()
    {
        string email = NewEmail();
        Guid admin = await Principals.UserIdAsync(fixture, Principal.Admin);
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var refusing = Substitute.For<IInviteAuthorizationPolicy>();
        refusing.CanInviteWithRoleAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns(false);
        // The service checks the policy again at the write; here it refuses there.
        var service = new InviteService(sp.GetRequiredService<ConnapseIdentityDbContext>(), sp.GetRequiredService<UserManager<ConnapseUser>>(), refusing, NullLogger<InviteService>.Instance);
        var store = ActivatorUtilities.CreateInstance<InviteStore>(sp, service);

        var created = await store.CreateAsync(email, "Admin", admin);

        created.Outcome.Should().Be(InviteWrite.Forbidden);
        created.Token.Should().BeNull();
        (await sp.GetRequiredService<ConnapseIdentityDbContext>().UserInvitations.CountAsync(i => i.Email == email)).Should().Be(0);
    }
}
