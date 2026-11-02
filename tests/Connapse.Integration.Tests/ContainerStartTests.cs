using System.Net;
using Docker.DotNet;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Unit")]
public class ContainerStartTests
{
    private static DockerImageNotFoundException ImageMissing() =>
        new(HttpStatusCode.NotFound, """{"message":"No such image: localstack/localstack:3"}""");

    [Fact]
    public async Task StartAsync_FirstPullLostTheImage_StartsAgainAndSucceeds()
    {
        var container = Substitute.For<IContainer>();
        container.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException(ImageMissing()), Task.CompletedTask);

        await ContainerStart.StartAsync(container);

        await container.Received(2).StartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_ImageNeverArrives_ThrowsAfterTheLastAttempt()
    {
        var container = Substitute.For<IContainer>();
        container.StartAsync(Arg.Any<CancellationToken>()).ThrowsAsync(ImageMissing());

        Func<Task> start = () => ContainerStart.StartAsync(container);

        await start.Should().ThrowAsync<DockerImageNotFoundException>();
        await container.Received(ContainerStart.MaxAttempts).StartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_OtherFailure_IsNotRetried()
    {
        var container = Substitute.For<IContainer>();
        container.StartAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("readiness check"));

        Func<Task> start = () => ContainerStart.StartAsync(container);

        await start.Should().ThrowAsync<TimeoutException>();
        await container.Received(1).StartAsync(Arg.Any<CancellationToken>());
    }
}
