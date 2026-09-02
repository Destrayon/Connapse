using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// How the AWS Access card decides which credential it is describing.
/// </summary>
/// <remarks>
/// The rule that matters is what it refuses to conclude. A resolving default-credential probe is not
/// proof of an instance role — it is equally an off-AWS box holding bootstrap access keys — so the
/// card must never read "a credential resolves" as "on AWS, nothing to do" and hide the Roles
/// Anywhere setup. Mode therefore turns only on whether a Roles Anywhere row is stored, never on the
/// probe.
/// </remarks>
public class AwsAccessCardTests
{
    [Fact]
    public void ResolveMode_BeforeRequirementsLoad_IsLoading() =>
        AwsAccessCard.ResolveMode(requirementsLoaded: false, hasStoredRolesAnywhere: false)
            .Should().Be(AwsAccessMode.Loading);

    [Fact]
    public void ResolveMode_LoadingWinsEvenWithAStoredRow() =>
        // Until the requirements have loaded there is nothing to act on; a stale row read must not
        // flash the configured card before the probe status is known.
        AwsAccessCard.ResolveMode(requirementsLoaded: false, hasStoredRolesAnywhere: true)
            .Should().Be(AwsAccessMode.Loading);

    [Fact]
    public void ResolveMode_WithStoredRow_IsRolesAnywhere() =>
        // Holds even when that row is revoked or failing: it is still the credential Connapse owns and
        // the only one it can reset, so the card stays in Roles Anywhere mode and offers re-setup.
        AwsAccessCard.ResolveMode(requirementsLoaded: true, hasStoredRolesAnywhere: true)
            .Should().Be(AwsAccessMode.RolesAnywhere);

    [Fact]
    public void ResolveMode_NoStoredRow_IsNotStored_SoSetupStaysOffered() =>
        // The off-AWS-with-bootstrap-credentials case: no stored row means offer setup, regardless of
        // whether a probe happens to resolve. That resolving probe is surfaced only as a note.
        AwsAccessCard.ResolveMode(requirementsLoaded: true, hasStoredRolesAnywhere: false)
            .Should().Be(AwsAccessMode.NotStored);
}
