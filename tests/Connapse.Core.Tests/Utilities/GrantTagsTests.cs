using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class GrantTagsTests
{
    [Fact]
    public void ManagedTag_IsTheConnapseProvenanceMarker()
    {
        // The one marker cleanup keys on. Kept out of the writer so create and the reconciler
        // cannot drift on the string that decides whether a grant is deletable.
        GrantTags.ManagedKey.Should().Be("connapse:managed");
        GrantTags.ManagedValue.Should().Be("true");
        GrantTags.ManagedKey.Should().NotStartWith("aws:"); // AWS rejects aws:-prefixed tag keys
    }
}
