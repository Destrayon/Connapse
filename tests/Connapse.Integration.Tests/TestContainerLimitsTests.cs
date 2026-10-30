using Docker.DotNet.Models;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Unit")]
public class TestContainerLimitsTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_Unset_DefaultsToTwoCpusAndFourGigabytes(string? value)
    {
        TestContainerLimits.Parse(value).Should().Be(new TestContainerLimits.Limits(2, 4 * GiB));
    }

    [Theory]
    [InlineData("off")]
    [InlineData(" OFF ")]
    public void Parse_Off_ReturnsNoLimits(string value)
    {
        TestContainerLimits.Parse(value).Should().BeNull();
    }

    [Theory]
    [InlineData("4,8g", 4, 8 * GiB)]
    [InlineData("1.5, 512m", 1.5, 512L * 1024 * 1024)]
    [InlineData("2,1048576k", 2, GiB)]
    [InlineData("2,1073741824", 2, GiB)]
    [InlineData("2,1073741824b", 2, GiB)]
    public void Parse_CpusAndMemory_ReturnsThoseLimits(string value, double cpus, long memoryBytes)
    {
        TestContainerLimits.Parse(value).Should().Be(new TestContainerLimits.Limits(cpus, memoryBytes));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2,4g,1")]
    [InlineData("0,4g")]
    [InlineData("-1,4g")]
    [InlineData("two,4g")]
    [InlineData("2,4t")]
    [InlineData("2,0g")]
    [InlineData("2,1m")]
    [InlineData("2,")]
    [InlineData("2,99999999999g")]
    [InlineData("none")]
    public void Parse_Unreadable_Throws(string value)
    {
        Action parse = () => TestContainerLimits.Parse(value);

        parse.Should().Throw<InvalidOperationException>().WithMessage($"*{TestContainerLimits.EnvironmentVariable}*");
    }

    [Fact]
    public void Apply_Limits_SetsCpuMemoryAndNoSwap()
    {
        var parameters = new CreateContainerParameters();

        TestContainerLimits.Apply(parameters, new TestContainerLimits.Limits(2, 4 * GiB));

        parameters.HostConfig.NanoCPUs.Should().Be(2_000_000_000);
        parameters.HostConfig.Memory.Should().Be(4 * GiB);
        parameters.HostConfig.MemorySwap.Should().Be(4 * GiB);
    }

    [Fact]
    public void Apply_NoLimits_LeavesParametersUntouched()
    {
        var parameters = new CreateContainerParameters();

        TestContainerLimits.Apply(parameters, null);

        parameters.HostConfig.Should().BeNull();
    }
}
