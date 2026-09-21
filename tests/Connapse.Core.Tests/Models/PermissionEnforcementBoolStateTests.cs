using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

/// <summary>
/// The Azure enforcement gate (<see cref="PermissionEnforcementSettings.StateForAzure"/>) reads its
/// own latch, and the SAML and Azure latches are independent: configuring one provider must never
/// change the enforcement state the other's resolver sees.
/// </summary>
[Trait("Category", "Unit")]
public class PermissionEnforcementBoolStateTests
{
    [Fact]
    public void Azure_Undetermined_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { AzureEnforcing = true };
        s.StateForAzure(azureConfigured: true, determined: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void Azure_NotLatched_IsNotEnforcing()
    {
        var s = new PermissionEnforcementSettings { AzureEnforcing = false };
        s.StateForAzure(azureConfigured: true).Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void Azure_Latched_ProviderConfigured_IsEnforcing()
    {
        var s = new PermissionEnforcementSettings { AzureEnforcing = true };
        s.StateForAzure(azureConfigured: true).Should().Be(EnforcementState.Enforcing);
    }

    [Fact]
    public void Azure_Latched_ProviderNotConfigured_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { AzureEnforcing = true };
        s.StateForAzure(azureConfigured: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void AzureLatch_DoesNotAffectTheSamlGate()
    {
        // Azure AD is latched and configured; SAML was never configured. The AWS/SAML gate must stay
        // NotEnforcing (its corpus unfiltered), not slide into EnforcingButUnusable. This is the
        // regression the shared single bit caused: configuring Azure hid every S3 document.
        var s = new PermissionEnforcementSettings { AzureEnforcing = true, IsEnforcing = false };

        s.StateFor(new SamlSignInSettings()).Should().Be(EnforcementState.NotEnforcing);
        s.StateForAzure(azureConfigured: true).Should().Be(EnforcementState.Enforcing);
    }

    [Fact]
    public void SamlLatch_DoesNotAffectTheAzureGate()
    {
        // The mirror: SAML latched and configured, Azure never configured. Azure's gate stays
        // NotEnforcing rather than denying every azblob document.
        var s = new PermissionEnforcementSettings { IsEnforcing = true, AzureEnforcing = false };

        s.StateForAzure(azureConfigured: false).Should().Be(EnforcementState.NotEnforcing);
    }
}
