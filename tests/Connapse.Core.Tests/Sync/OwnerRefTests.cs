using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sync;

[Trait("Category", "Unit")]
public class OwnerRefTests
{
    [Fact]
    public void ForContainer_IsNotASource()
    {
        var id = Guid.NewGuid();

        var owner = OwnerRef.ForContainer(id);

        owner.Id.Should().Be(id);
        owner.IsSource.Should().BeFalse();
    }

    [Fact]
    public void ForSource_IsASource()
    {
        var id = Guid.NewGuid();

        var owner = OwnerRef.ForSource(id);

        owner.Id.Should().Be(id);
        owner.IsSource.Should().BeTrue();
    }

    [Fact]
    public void ContainerId_And_SourceId_AreMutuallyExclusive()
    {
        // These map straight onto the ck_documents_single_owner CHECK: exactly one of the
        // two columns is set, and this type is what decides which.
        var container = OwnerRef.ForContainer(Guid.NewGuid());
        var source = OwnerRef.ForSource(Guid.NewGuid());

        container.ContainerId.Should().NotBeNull();
        container.SourceId.Should().BeNull();
        source.ContainerId.Should().BeNull();
        source.SourceId.Should().NotBeNull();
    }
}
