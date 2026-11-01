using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using NSubstitute;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The providers page's check of whether a save landed after a later step threw. settings.get never
/// returns secrets, so a save that carried one can't be confirmed from what it returns.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InEffectMatchesTests
{
    private static readonly AzureProviderSettings Stored = new() { TenantId = "tenant", ClientId = "client", ClientCertificatePath = "/certs/a.pfx", ClientCertificatePassword = "old" };

    private static GetSettings Reading(AzureProviderSettings stored)
    {
        var store = Substitute.For<ISettingsStore>();
        store.GetWithRevisionAsync<AzureProviderSettings>(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((stored, "1"));
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        return new GetSettings(store, Substitute.For<IConfiguredSettings>(), authorizer);
    }

    [Fact]
    public async Task InEffectMatchesAsync_PasswordOnlyChangeThatNeverLanded_IsUnknown()
    {
        bool? matches = await Reading(Stored).InEffectMatchesAsync(new SignedIn(), "azure", Stored with { ClientCertificatePassword = "new" });

        matches.Should().BeNull("whether the new password was stored can't be read back");
    }

    [Fact]
    public async Task InEffectMatchesAsync_NoSecretAndTheSameSettings_Matches()
    {
        bool? matches = await Reading(Stored).InEffectMatchesAsync(new SignedIn(), "azure", Stored with { ClientCertificatePassword = null });

        matches.Should().BeTrue();
    }

    [Fact]
    public async Task InEffectMatchesAsync_NoSecretAndOtherSettings_DoesNotMatch()
    {
        bool? matches = await Reading(Stored).InEffectMatchesAsync(new SignedIn(), "azure", Stored with { ClientCertificatePassword = null, ClientId = "other" });

        matches.Should().BeFalse();
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
    }
}
