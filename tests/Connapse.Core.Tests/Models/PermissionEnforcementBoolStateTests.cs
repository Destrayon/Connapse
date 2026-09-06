using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class PermissionEnforcementBoolStateTests
{
    [Fact]
    public void Undetermined_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: true, determined: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void NotLatched_IsNotEnforcing()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = false };
        s.StateFor(providerConfigured: true).Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void Latched_ProviderConfigured_IsEnforcing()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: true).Should().Be(EnforcementState.Enforcing);
    }

    [Fact]
    public void Latched_ProviderNotConfigured_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }
}
