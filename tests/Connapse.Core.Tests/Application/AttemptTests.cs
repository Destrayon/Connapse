using Connapse.Application;
using Connapse.Application.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Core.Tests.Application;

[Trait("Category", "Unit")]
public class AttemptTests
{
    public enum SampleStatus
    {
        Success,
        NotFound,
        Conflict,
    }

    [Fact]
    public void Succeed_ResultGiven_SucceedsWithIt()
    {
        var attempt = Attempt<string, SampleStatus>.Succeed("done");

        attempt.Succeeded.Should().BeTrue();
        attempt.Status.Should().Be(SampleStatus.Success);
        attempt.Result.Should().Be("done");
    }

    [Theory]
    [InlineData(SampleStatus.NotFound)]
    [InlineData(SampleStatus.Conflict)]
    public void Fail_FailureStatus_FailsWithNoResult(SampleStatus status)
    {
        var attempt = Attempt<string, SampleStatus>.Fail(status);

        attempt.Succeeded.Should().BeFalse();
        attempt.Status.Should().Be(status);
        attempt.Result.Should().BeNull();
    }

    [Fact]
    public void Fail_SuccessStatus_Throws()
    {
        var fail = () => Attempt<string, SampleStatus>.Fail(SampleStatus.Success);

        fail.Should().Throw<ArgumentException>().WithParameterName("status");
    }

    [Fact]
    public void AddApplication_RegistersEveryOperationInTheAssembly()
    {
        var services = new ServiceCollection().AddApplication();
        var operations = ServiceCollectionExtensions.Operations(typeof(OperationAttribute).Assembly).ToList();

        services.Select(d => d.ServiceType).Should().BeEquivalentTo(operations);
        services.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
    }
}
