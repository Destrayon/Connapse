using System.Reflection;
using Connapse.Core;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The two per-user-permission resets must not revert a change another administrator made while
/// this page was open.
/// </summary>
/// <remarks>
/// Both resets rebuild the whole <see cref="SamlSignInSettings"/> from a single source and save it.
/// The bug they guard against is building from <c>samlSettings</c> — the copy captured once in
/// <c>OnInitialized</c> — because a long-lived page would then write a page-load snapshot back over
/// a certificate rotation or a group change made since. The fix builds from
/// <c>SamlOptions.CurrentValue</c>, the live monitor value that reflects reloads from concurrent
/// saves. These tests pin both the field-level transforms and the wiring that feeds them.
/// </remarks>
[Trait("Category", "Unit")]
public class SamlResetConcurrencyTests
{
    private static SamlSignInSettings Full(string certificate = "CERT", string groupId = "group-1") => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = certificate,
        GrantGroupId = groupId,
        GrantGroupName = "Everyone",
    };

    private static SamlSignInSettings Invoke(string helper, SamlSignInSettings input)
    {
        MethodInfo? method = typeof(Providers).GetMethod(
            helper, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"{helper} should exist as a private static helper on Providers");
        return (SamlSignInSettings)method!.Invoke(null, [input])!;
    }

    [Fact]
    public void WithoutGrantGroup_KeepsTheApplicationAndDropsTheGroup()
    {
        SamlSignInSettings source = Full();
        SamlSignInSettings result = Invoke("WithoutGrantGroup", source);

        result.EntityId.Should().Be(source.EntityId);
        result.AcsUrl.Should().Be(source.AcsUrl);
        result.IdpEntityId.Should().Be(source.IdpEntityId);
        result.IdpSingleSignOnUrl.Should().Be(source.IdpSingleSignOnUrl);
        result.IdpSigningCertificate.Should().Be(source.IdpSigningCertificate);
        result.HasGrantGroup.Should().BeFalse("clearing the group is the whole point of this reset");
    }

    [Fact]
    public void GrantGroupOnly_KeepsTheGroupAndDropsTheApplication()
    {
        SamlSignInSettings source = Full();
        SamlSignInSettings result = Invoke("GrantGroupOnly", source);

        result.GrantGroupId.Should().Be(source.GrantGroupId);
        result.GrantGroupName.Should().Be(source.GrantGroupName);
        result.IsConfigured.Should().BeFalse("clearing the SAML application is the whole point of this reset");
    }

    [Fact]
    public void GroupReset_TakesTheCertificateFromWhicheverSettingsItIsGiven()
    {
        // The reset feeds this helper SamlOptions.CurrentValue (the live value). Given the freshly
        // rotated settings it carries the rotated certificate through; given a stale snapshot it
        // would carry the old one. That difference is exactly why the reset must read the live
        // value rather than the page-load snapshot.
        Invoke("WithoutGrantGroup", Full(certificate: "ROTATED")).IdpSigningCertificate
            .Should().Be("ROTATED");
        Invoke("WithoutGrantGroup", Full(certificate: "STALE")).IdpSigningCertificate
            .Should().Be("STALE");
    }

    [Fact]
    public void ApplicationReset_TakesTheGroupFromWhicheverSettingsItIsGiven()
    {
        Invoke("GrantGroupOnly", Full(groupId: "rotated-group")).GrantGroupId
            .Should().Be("rotated-group");
        Invoke("GrantGroupOnly", Full(groupId: "stale-group")).GrantGroupId
            .Should().Be("stale-group");
    }

    /// <summary>
    /// The wiring the transforms depend on: both resets must read the live monitor value, not the
    /// <c>samlSettings</c> snapshot. A transform that is correct but fed the stale copy reverts a
    /// concurrent change just as surely, and that mistake is invisible to the transform tests above.
    /// </summary>
    [Fact]
    public void BothSamlResets_BuildFromTheLiveSettingsNotThePageSnapshot()
    {
        string source = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "Providers.razor"));

        MethodBody(source, "private async Task ClearGrantGroup()")
            .Should().Contain("SamlOptions.CurrentValue");
        MethodBody(source, "private async Task ClearSamlApplication()")
            .Should().Contain("SamlOptions.CurrentValue");
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BePositive($"{signature} should exist");
        int next = source.IndexOf("private ", start + signature.Length, StringComparison.Ordinal);
        return next > start ? source[start..next] : source[start..];
    }
}
