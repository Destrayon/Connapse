using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Settings;

[Trait("Category", "Unit")]
public class AzureAdSignInSettingsTests
{
    private static AzureAdSignInSettings CreateConfigured() => new()
    {
        TenantId = "tenant-id",
        ClientId = "client-id",
        RedirectUri = "https://connapse.example.com/signin-oidc",
        ClientCertificatePath = "/certs/entra.pem",
    };

    [Fact]
    public void IsConfigured_AllRequiredFieldsSet_ReturnsTrue()
    {
        var settings = CreateConfigured();

        settings.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_ClientCertificatePasswordOmitted_StillReturnsTrue()
    {
        var settings = CreateConfigured() with { ClientCertificatePassword = null };

        settings.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_TenantIdBlank_ReturnsFalse()
    {
        var settings = CreateConfigured() with { TenantId = "  " };

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ClientIdBlank_ReturnsFalse()
    {
        var settings = CreateConfigured() with { ClientId = null };

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_RedirectUriBlank_ReturnsFalse()
    {
        var settings = CreateConfigured() with { RedirectUri = string.Empty };

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_ClientCertificatePathBlank_ReturnsFalse()
    {
        var settings = CreateConfigured() with { ClientCertificatePath = null };

        settings.IsConfigured.Should().BeFalse();
    }
}
