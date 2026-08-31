using FluentAssertions;

namespace Connapse.Core.Tests.Models;

/// <summary>
/// Whether searches are filtered, which is deliberately not the same question as whether sign-in
/// is set up.
/// </summary>
/// <remarks>
/// They used to be one test. That meant the five SAML fields decided both, so clearing one of them
/// switched filtering off — and the ordinary reason to clear one is pasting a rotated signing
/// certificate, which breaks sign-in at the same moment and so leaves nobody looking at search
/// results while the corpus is open.
/// </remarks>
[Trait("Category", "Unit")]
public class SamlEnforcementStateTests
{
    private static SamlSignInSettings Complete() => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = "MIIDBTCCAe2gAwIBAgIFEXAMPLE",
    };

    [Fact]
    public void FreshInstallation_IsNotEnforcing()
    {
        // Filtering is opt-in. Every deployment that upgrades without setting this up must keep
        // searching exactly as it did.
        new SamlSignInSettings().Enforcement.Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void ConfiguredButNeverLatched_IsNotEnforcing()
    {
        // Configuration alone no longer switches filtering on. The startup latch is what turns a
        // working configuration into enforcement, so that the decision is recorded rather than
        // inferred from fields that can be edited.
        Complete().Enforcement.Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void ConfiguredAndLatched_IsEnforcing()
    {
        var settings = Complete();
        settings.EnforcementEnabled = true;

        settings.Enforcement.Should().Be(EnforcementState.Enforcing);
    }

    [Theory]
    [InlineData("EntityId")]
    [InlineData("AcsUrl")]
    [InlineData("IdpEntityId")]
    [InlineData("IdpSingleSignOnUrl")]
    [InlineData("IdpSigningCertificate")]
    public void EnforcingWithAnyFieldBlanked_IsUnusableRatherThanOff(string field)
    {
        // Every one of the five, because the bug was that any single blank field opened the corpus
        // and only one of them is the one somebody actually edits.
        var settings = Complete();
        settings.EnforcementEnabled = true;

        typeof(SamlSignInSettings).GetProperty(field)!.SetValue(settings, string.Empty);

        settings.Enforcement.Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void ForgottenConfiguration_IsNotEnforcing()
    {
        // What "forget this configuration" leaves behind: nothing set and nothing enforced. The
        // deliberate way to stop filtering, as opposed to the accidental one.
        new SamlSignInSettings { EnforcementEnabled = false }
            .Enforcement.Should().Be(EnforcementState.NotEnforcing);
    }
}
