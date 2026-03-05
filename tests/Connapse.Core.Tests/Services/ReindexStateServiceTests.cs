using Connapse.Web.Services;
using FluentAssertions;

namespace Connapse.Core.Tests.Services;

[Trait("Category", "Unit")]
public class ReindexStateServiceTests
{
    [Fact]
    public void Initial_State_IsIdle()
    {
        var sut = new ReindexStateService();

        sut.Current.Status.Should().Be(ReindexStatus.Idle);
        sut.Current.StartedAt.Should().BeNull();
        sut.Current.CompletedAt.Should().BeNull();
        sut.Current.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkStarted_TransitionsToRunning()
    {
        var sut = new ReindexStateService();

        sut.MarkStarted();

        sut.Current.Status.Should().Be(ReindexStatus.Running);
        sut.Current.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        sut.Current.CompletedAt.Should().BeNull();
        sut.Current.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkCompleted_TransitionsToCompleted()
    {
        var sut = new ReindexStateService();

        sut.MarkStarted();
        sut.MarkCompleted();

        sut.Current.Status.Should().Be(ReindexStatus.Completed);
        sut.Current.StartedAt.Should().NotBeNull();
        sut.Current.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        sut.Current.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkFailed_TransitionsToFailed_WithError()
    {
        var sut = new ReindexStateService();

        sut.MarkStarted();
        sut.MarkFailed("Something went wrong");

        sut.Current.Status.Should().Be(ReindexStatus.Failed);
        sut.Current.StartedAt.Should().NotBeNull();
        sut.Current.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        sut.Current.LastError.Should().Be("Something went wrong");
    }

    [Fact]
    public void MarkStarted_ClearsPreviousFailure()
    {
        var sut = new ReindexStateService();

        sut.MarkStarted();
        sut.MarkFailed("first failure");

        // Start a new run — previous failure should be cleared
        sut.MarkStarted();

        sut.Current.Status.Should().Be(ReindexStatus.Running);
        sut.Current.LastError.Should().BeNull();
        sut.Current.CompletedAt.Should().BeNull();
    }
}
