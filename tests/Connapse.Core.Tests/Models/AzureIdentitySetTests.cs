using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class AzureIdentitySetTests
{
    [Fact]
    public void Resolved_IsEnabled_AndCarriesPrincipals()
    {
        AzureIdentitySet set = AzureIdentitySet.Resolved(["oid-1", "group-a"]);

        set.Enabled.Should().BeTrue();
        set.Outcome.Should().Be(AzureIdentityOutcome.Resolved);
        set.PrincipalOids.Should().Equal("oid-1", "group-a");
    }

    [Fact]
    public void Deprovisioned_And_Failed_DenyWithNoPrincipals()
    {
        AzureIdentitySet.Deprovisioned().Enabled.Should().BeFalse();
        AzureIdentitySet.Deprovisioned().Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
        AzureIdentitySet.Deprovisioned().PrincipalOids.Should().BeEmpty();

        AzureIdentitySet.Failed().Enabled.Should().BeFalse();
        AzureIdentitySet.Failed().Outcome.Should().Be(AzureIdentityOutcome.Failed);
        AzureIdentitySet.Failed().PrincipalOids.Should().BeEmpty();
    }
}
