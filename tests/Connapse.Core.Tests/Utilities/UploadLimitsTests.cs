using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public sealed class UploadLimitsTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void RequestBytes_DefaultLimit_LeavesRoomForTheForm() =>
        UploadLimits.RequestBytes(256 * MiB).Should().Be((256 * MiB) + UploadLimits.FormOverheadBytes);

    [Fact]
    public void RequestBytes_LowLimit_StaysAtTheServersDefault() =>
        UploadLimits.RequestBytes(MiB).Should().Be(UploadLimits.SmallestRequestBytes);

    [Fact]
    public void RequestBytes_HighestLimit_DoesNotOverflow() =>
        UploadLimits.RequestBytes(long.MaxValue).Should().Be(long.MaxValue);

    [Fact]
    public void FileTooLarge_OneByteOver_DoesNotReadAsAtTheLimit() =>
        UploadLimits.FileTooLarge((256 * MiB) + 1, 256 * MiB).Should().Be("The file is 256.1 MB, over the 256 MB limit.");
}
