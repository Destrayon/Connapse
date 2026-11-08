using Connapse.Core;
using Connapse.Core.Interfaces;
using NSubstitute;

namespace Connapse.Tests.Support;

/// <summary>
/// A substitute container store whose viewer-aware lookups answer what the team-only ones are stubbed to, so a test
/// that stubs <c>GetAsync(id)</c> or <c>GetByNameAsync(name)</c> keeps working when the code under test asks as a viewer
/// (CON-235). Tests about personal spaces stub the viewer overloads directly, which replaces the forwarding.
/// </summary>
public static class ContainerStoreFakes
{
    public static IContainerStore Create()
    {
        IContainerStore store = Substitute.For<IContainerStore>();
        store.GetAsync(Arg.Any<Guid>(), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
            .Returns(call => store.GetAsync(call.ArgAt<Guid>(0), call.ArgAt<CancellationToken>(2)));
        store.FindByNameAsync(Arg.Any<string>(), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
            .Returns(async call => await store.GetByNameAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(2)) is { } found
                ? (IReadOnlyList<Container>)[found]
                : []);
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<MemoryCaller?>(), Arg.Any<CancellationToken>())
            .Returns(call => store.ListAsync(call.ArgAt<int>(0), call.ArgAt<int>(1), call.ArgAt<CancellationToken>(3)));
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<MemoryCaller?>(), Arg.Any<ContainerKind>(), Arg.Any<CancellationToken>())
            .Returns(async call => (IReadOnlyList<Container>)[.. (await store.ListAsync(
                    call.ArgAt<int>(0), call.ArgAt<int>(1), call.ArgAt<MemoryCaller?>(2), call.ArgAt<CancellationToken>(4)))
                .Where(c => c.Kind == call.ArgAt<ContainerKind>(3))]);
        return store;
    }
}
