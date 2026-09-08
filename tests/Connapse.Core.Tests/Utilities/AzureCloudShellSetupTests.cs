using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AzureCloudShellSetupTests
{
    private static AzureSetupInput Input() => new(
        SubscriptionId: "33333333-3333-3333-3333-333333333333",
        StorageScope: null,
        RedirectUri: "https://connapse.example.com/api/v1/auth/cloud/azure/callback",
        AccessAppName: "Connapse-Azure-Access",
        SignInAppName: "Connapse-Azure-SignIn",
        PublicCertificatePem: "-----BEGIN CERTIFICATE-----\nMIIBfakefakefake\n-----END CERTIFICATE-----");

    [Fact]
    public void GenerateScript_ContainsTheKeyStepsAndMarkers()
    {
        string script = AzureCloudShellSetup.GenerateScript(Input());

        script.Should().Contain(AzureCloudShellSetup.BeginMarker);
        script.Should().Contain(AzureCloudShellSetup.EndMarker);
        script.Should().Contain("33333333-3333-3333-3333-333333333333");
        script.Should().Contain("https://connapse.example.com/api/v1/auth/cloud/azure/callback");
        script.Should().Contain("az ad app create --display-name \"$ACCESS_APP_NAME\"");
        script.Should().Contain("az ad app create --display-name \"$SIGNIN_APP_NAME\"");
        script.Should().Contain("--web-redirect-uris \"$REDIRECT_URI\"");
        script.Should().Contain(AzureCloudShellSetup.BlobDataRoleName);
        script.Should().Contain(AzureCloudShellSetup.RbacReadRoleName);
        script.Should().Contain("blobs/tags/read");
        script.Should().Contain("Microsoft.Authorization/denyAssignments/read");
        script.Should().Contain("User.Read.All");
        script.Should().Contain("GroupMember.Read.All");
        script.Should().Contain("az ad app permission admin-consent");
        script.Should().Contain("MIIBfakefakefake"); // the public cert is embedded
        script.Should().NotContain("PRIVATE KEY");    // never the private key
    }

    [Fact]
    public void GenerateScript_StorageScope_DefaultsToSubscription_WhenNull()
    {
        string script = AzureCloudShellSetup.GenerateScript(Input());
        script.Should().Contain("STORAGE_SCOPE='/subscriptions/33333333-3333-3333-3333-333333333333'");
    }

    [Fact]
    public void GenerateScript_UsesGivenStorageScope_WhenProvided()
    {
        var input = Input() with { StorageScope = "/subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct" };
        string script = AzureCloudShellSetup.GenerateScript(input);
        script.Should().Contain("STORAGE_SCOPE='/subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct'");
    }

    private static string Block(string tenant, string sub, string access, string signIn, string consent = "true", string? url = null) =>
        $"{AzureCloudShellSetup.BeginMarker}\n"
        + $"tenantId={tenant}\nsubscriptionId={sub}\naccessAppClientId={access}\nsignInAppClientId={signIn}\n"
        + $"consentGranted={consent}\nconsentUrl={url ?? "https://login.microsoftonline.com/" + tenant + "/adminconsent?client_id=" + access}\n"
        + $"{AzureCloudShellSetup.EndMarker}";

    [Fact]
    public void ParseResult_WellFormedBlock_ReturnsAllFields()
    {
        AzureSetupResult? r = AzureCloudShellSetup.ParseResult(Block(
            "11111111-1111-1111-1111-111111111111", "33333333-3333-3333-3333-333333333333",
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444"));

        r.Should().NotBeNull();
        r!.TenantId.Should().Be("11111111-1111-1111-1111-111111111111");
        r.SubscriptionId.Should().Be("33333333-3333-3333-3333-333333333333");
        r.AccessAppClientId.Should().Be("22222222-2222-2222-2222-222222222222");
        r.SignInAppClientId.Should().Be("44444444-4444-4444-4444-444444444444");
        r.ConsentGranted.Should().BeTrue();
        r.ConsentUrl.Should().Contain("adminconsent");
    }

    [Fact]
    public void ParseResult_ConsentNotGranted_CarriesFalseAndUrl()
    {
        AzureSetupResult? r = AzureCloudShellSetup.ParseResult(Block(
            "11111111-1111-1111-1111-111111111111", "33333333-3333-3333-3333-333333333333",
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444",
            consent: "false"));

        r!.ConsentGranted.Should().BeFalse();
        r.ConsentUrl.Should().NotBeNull();
    }

    [Fact]
    public void ParseResult_AnchorsOnLastMarkerPair_WhenTerminalIsPasted()
    {
        // Pasting the whole terminal (script echo THEN the printed block) must read the printed block.
        string terminal = "az ad app create ...\n" + AzureCloudShellSetup.BeginMarker + "\nnoise\n" + AzureCloudShellSetup.EndMarker
            + "\n...output...\n" + Block("11111111-1111-1111-1111-111111111111", "33333333-3333-3333-3333-333333333333",
                "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444");

        AzureCloudShellSetup.ParseResult(terminal)!.AccessAppClientId
            .Should().Be("22222222-2222-2222-2222-222222222222");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no markers here")]
    public void ParseResult_NoBlock_ReturnsNull(string? pasted) =>
        AzureCloudShellSetup.ParseResult(pasted).Should().BeNull();

    [Fact]
    public void ParseResult_NonGuidId_ReturnsNull() =>
        AzureCloudShellSetup.ParseResult(Block(
            "not-a-guid", "33333333-3333-3333-3333-333333333333",
            "22222222-2222-2222-2222-222222222222", "44444444-4444-4444-4444-444444444444"))
            .Should().BeNull();

    [Fact]
    public void GenerateScript_ThenParseResult_RoundTripsTheMarkerContract()
    {
        // The script's printf uses the same keys ParseResult reads — guard against them drifting apart.
        string script = AzureCloudShellSetup.GenerateScript(Input());
        foreach (string key in new[] { "tenantId=", "subscriptionId=", "accessAppClientId=", "signInAppClientId=", "consentGranted=", "consentUrl=" })
            script.Should().Contain(key);
    }
}
