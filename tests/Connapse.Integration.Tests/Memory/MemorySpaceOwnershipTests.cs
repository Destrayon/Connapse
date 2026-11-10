using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-314: the rename and delete the Memory page offers never reach someone else's personal space. The operations run
/// over the real container and space stores, as a caller every permission check passes, so ownership alone refuses.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class MemorySpaceOwnershipTests(SharedWebAppFixture fixture)
{
    private static OperationContext As(Guid user) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "test")), OperationSurface.Ui);

    private static IOperationAuthorizer AllowsEverything()
    {
        IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        return authorizer;
    }

    [Fact]
    public async Task RenameAndDelete_SomeoneElsesPersonalSpace_AreRefusedAndLeaveItAsItWas()
    {
        Guid owner = Guid.NewGuid(), other = Guid.NewGuid();
        string name = $"own-{Guid.NewGuid():N}"[..20];
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        IContainerStore containers = services.GetRequiredService<IContainerStore>();
        IMemorySpaceStore spaces = services.GetRequiredService<IMemorySpaceStore>();
        UpdateMemorySpace update = ActivatorUtilities.CreateInstance<UpdateMemorySpace>(services, AllowsEverything());
        DeleteMemorySpace delete = ActivatorUtilities.CreateInstance<DeleteMemorySpace>(services, AllowsEverything());
        Container mine = (await spaces.CreatePersonalAsync(new MemoryCaller(owner, null, null), name, "the owner's notes"))!;
        Guid id = Guid.Parse(mine.Id);
        try
        {
            var renamed = await update.RunAsync(As(other), id, new MemorySpaceChanges($"{name}-x", "taken over"));
            var counted = await delete.RunAsync(As(other), id, dryRun: true);
            var deleted = await delete.RunAsync(As(other), id, dryRun: false);

            renamed.Status.Should().Be(MemorySpaceStatus.NotFound, "someone else's personal space reads as missing");
            counted.Status.Should().Be(MemorySpaceStatus.NotFound, "not even its fact count shows");
            deleted.Status.Should().Be(MemorySpaceStatus.NotFound);
            Container? still = await containers.GetAsync(id, new MemoryCaller(owner, null, null));
            still.Should().NotBeNull("the space is not deleted");
            still!.Name.Should().Be(name, "the space is not renamed");
            still.Description.Should().Be("the owner's notes");

            (await update.RunAsync(As(owner), id, new MemorySpaceChanges($"{name}-y"))).Status
                .Should().Be(MemorySpaceStatus.Success, "its owner may rename it");
        }
        finally
        {
            (await delete.RunAsync(As(owner), id, dryRun: false)).Status.Should().Be(MemorySpaceStatus.Success, "its owner may delete it");
        }
    }
}
