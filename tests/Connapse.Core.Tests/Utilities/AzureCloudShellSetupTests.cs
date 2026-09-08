using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class AzureCloudShellSetupTests
{
    private const string Sub = "33333333-3333-3333-3333-333333333333";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string AccessId = "22222222-2222-2222-2222-222222222222";
    private const string SignInId = "44444444-4444-4444-4444-444444444444";
    private const string Cert = "-----BEGIN CERTIFICATE-----\nMIIBfakefakefake\n-----END CERTIFICATE-----";

    // ---- Access step ----

    private static AzureAccessSetupInput AccessInput(string? scope = null) =>
        new(Sub, scope, "Connapse-Azure-Access", Cert);

    [Fact]
    public void AccessScript_ContainsRolesAppCertAndMarkers_ButNeverThePrivateKey()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());

        s.Should().Contain(AzureCloudShellSetup.AccessBeginMarker).And.Contain(AzureCloudShellSetup.AccessEndMarker);
        s.Should().Contain($"SUBSCRIPTION_ID='{Sub}'");
        s.Should().Contain(AzureCloudShellSetup.BlobDataRoleName).And.Contain("blobs/tags/read");
        s.Should().Contain(AzureCloudShellSetup.RbacReadRoleName).And.Contain("Microsoft.Authorization/denyAssignments/read");
        s.Should().Contain("az ad app create --display-name \"$ACCESS_APP_NAME\"");
        s.Should().Contain("az ad app credential reset").And.Contain("--append");
        s.Should().Contain("MIIBfakefakefake");
        s.Should().NotContain("PRIVATE KEY");
        s.Should().NotContain("admin-consent"); // consent belongs to the Permissions step
    }

    [Fact]
    public void AccessScript_StorageScope_DefaultsToSubscription() =>
        AzureCloudShellSetup.GenerateAccessScript(AccessInput())
            .Should().Contain($"STORAGE_SCOPE='/subscriptions/{Sub}'");

    [Fact]
    public void AccessScript_UsesGivenStorageScope()
    {
        string scope = $"/subscriptions/{Sub}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
        AzureCloudShellSetup.GenerateAccessScript(AccessInput(scope))
            .Should().Contain($"STORAGE_SCOPE='{scope}'");
    }

    private static string AccessBlock(string tenant = Tenant, string sub = Sub, string access = AccessId) =>
        $"{AzureCloudShellSetup.AccessBeginMarker}\ntenantId={tenant}\nsubscriptionId={sub}\naccessAppClientId={access}\n{AzureCloudShellSetup.AccessEndMarker}";

    [Fact]
    public void ParseAccessResult_WellFormed_ReturnsIds()
    {
        AzureAccessResult? r = AzureCloudShellSetup.ParseAccessResult(AccessBlock());
        r.Should().NotBeNull();
        r!.TenantId.Should().Be(Tenant);
        r.SubscriptionId.Should().Be(Sub);
        r.AccessAppClientId.Should().Be(AccessId);
    }

    [Fact]
    public void ParseAccessResult_AnchorsOnLastMarkerPair_WhenTerminalIsPasted()
    {
        string terminal = "echo script\n" + AzureCloudShellSetup.AccessBeginMarker + "\nnoise\n" + AzureCloudShellSetup.AccessEndMarker
            + "\n...\n" + AccessBlock();
        AzureCloudShellSetup.ParseAccessResult(terminal)!.AccessAppClientId.Should().Be(AccessId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no markers")]
    public void ParseAccessResult_NoBlock_ReturnsNull(string? pasted) =>
        AzureCloudShellSetup.ParseAccessResult(pasted).Should().BeNull();

    [Fact]
    public void ParseAccessResult_NonGuid_ReturnsNull() =>
        AzureCloudShellSetup.ParseAccessResult(AccessBlock(tenant: "not-a-guid")).Should().BeNull();

    [Fact]
    public void AccessScript_PrintsTheKeys_ParseAccessResultReads()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());
        foreach (string key in new[] { "tenantId=", "subscriptionId=", "accessAppClientId=" })
            s.Should().Contain(key);
    }

    // ---- Per-user permissions step ----

    private static AzurePermissionsSetupInput PermissionsInput() =>
        new(AccessId, "https://connapse.example.com/api/v1/auth/cloud/azure/callback", "Connapse-Azure-SignIn", Cert);

    [Fact]
    public void PermissionsScript_ContainsSignInAppGraphPermsConsentAndMarkers()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());

        s.Should().Contain(AzureCloudShellSetup.PermissionsBeginMarker).And.Contain(AzureCloudShellSetup.PermissionsEndMarker);
        s.Should().Contain($"ACCESS_APP_ID='{AccessId}'");
        s.Should().Contain("--web-redirect-uris \"$REDIRECT_URI\"");
        s.Should().Contain("https://connapse.example.com/api/v1/auth/cloud/azure/callback");
        s.Should().Contain("User.Read.All").And.Contain("GroupMember.Read.All");
        s.Should().Contain("az ad app permission admin-consent");
        s.Should().Contain("/adminconsent?client_id=");
        s.Should().Contain("MIIBfakefakefake");
        s.Should().NotContain("PRIVATE KEY");
        s.Should().NotContain("az role definition create"); // roles belong to the Access step
    }

    private static string PermissionsBlock(string signIn = SignInId, string consent = "true") =>
        $"{AzureCloudShellSetup.PermissionsBeginMarker}\nsignInAppClientId={signIn}\nconsentGranted={consent}\n"
        + $"consentUrl=https://login.microsoftonline.com/{Tenant}/adminconsent?client_id={AccessId}\n{AzureCloudShellSetup.PermissionsEndMarker}";

    [Fact]
    public void ParsePermissionsResult_ConsentGranted_ReturnsIdAndTrue()
    {
        AzurePermissionsResult? r = AzureCloudShellSetup.ParsePermissionsResult(PermissionsBlock());
        r.Should().NotBeNull();
        r!.SignInAppClientId.Should().Be(SignInId);
        r.ConsentGranted.Should().BeTrue();
        r.ConsentUrl.Should().Contain("adminconsent");
    }

    [Fact]
    public void ParsePermissionsResult_ConsentPending_ReturnsFalseAndUrl()
    {
        AzurePermissionsResult? r = AzureCloudShellSetup.ParsePermissionsResult(PermissionsBlock(consent: "false"));
        r!.ConsentGranted.Should().BeFalse();
        r.ConsentUrl.Should().NotBeNull();
    }

    [Fact]
    public void ParsePermissionsResult_NonGuid_ReturnsNull() =>
        AzureCloudShellSetup.ParsePermissionsResult(PermissionsBlock(signIn: "bad")).Should().BeNull();

    [Fact]
    public void ParsePermissionsResult_DoesNotReadTheAccessBlock() =>
        AzureCloudShellSetup.ParsePermissionsResult(AccessBlock()).Should().BeNull();

    [Fact]
    public void PermissionsScript_PrintsTheKeys_ParsePermissionsResultReads()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());
        foreach (string key in new[] { "signInAppClientId=", "consentGranted=", "consentUrl=" })
            s.Should().Contain(key);
    }
}
