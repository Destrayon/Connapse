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

    private static PermissionEnforcementSettings Enforcing(bool on = true) => new() { IsEnforcing = on };

    [Fact]
    public void FreshInstallation_IsNotEnforcing()
    {
        // Filtering is opt-in. Every deployment that upgrades without setting this up must keep
        // searching exactly as it did.
        Enforcing(false).StateFor(new SamlSignInSettings())
            .Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void ConfiguredButNeverLatched_IsNotEnforcing()
    {
        // Configuration alone no longer switches filtering on. The startup migration is what turns
        // a working configuration into enforcement, so that the decision is recorded rather than
        // inferred from fields that can be edited.
        Enforcing(false).StateFor(Complete()).Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void ConfiguredAndLatched_IsEnforcing() =>
        Enforcing().StateFor(Complete()).Should().Be(EnforcementState.Enforcing);

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
        typeof(SamlSignInSettings).GetProperty(field)!.SetValue(settings, string.Empty);

        Enforcing().StateFor(settings).Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void ForgottenConfiguration_IsNotEnforcing()
    {
        // What "forget this configuration" leaves behind: nothing set and nothing enforced. The
        // deliberate way to stop filtering, as opposed to the accidental one.
        Enforcing(false).StateFor(new SamlSignInSettings())
            .Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void AnUndeterminedMigration_Enforces_EvenWithNothingConfigured()
    {
        // The startup migration could not find out whether this deployment was filtering. Not
        // knowing is not permission to open: the first version of that migration treated a failed
        // read as "was not enforcing", which turned one database blip at boot into the whole corpus
        // being readable.
        Enforcing(false).StateFor(new SamlSignInSettings(), determined: false)
            .Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void AnUndeterminedMigration_OutranksACompleteConfiguration()
    {
        Enforcing().StateFor(Complete(), determined: false)
            .Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void EnforcementIsNotReadFromTheSignInSettings()
    {
        // The marker lives in its own settings category so that recording it never rewrites a SAML
        // value. A deployment configured through environment variables would otherwise have had
        // those values copied into a database row that then shadowed them permanently.
        typeof(SamlSignInSettings).GetProperties()
            .Select(prop => prop.Name)
            .Should().NotContain(name => name.Contains("Enforce", StringComparison.Ordinal));
    }
}
