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

    private static AzureAccessSetupInput AccessInput() => new("Connapse-Azure-Access", Cert);

    [Fact]
    public void AccessScript_ContainsRolesAppCertAndMarkers_ButNeverThePrivateKey()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());

        s.Should().Contain(AzureCloudShellSetup.AccessBeginMarker).And.Contain(AzureCloudShellSetup.AccessEndMarker);
        s.Should().Contain("ACCESS_APP_NAME='Connapse-Azure-Access'");
        s.Should().Contain(AzureCloudShellSetup.BlobDataRoleName).And.Contain("blobs/tags/read");
        s.Should().Contain(AzureCloudShellSetup.RbacReadRoleName).And.Contain("Microsoft.Authorization/denyAssignments/read");
        s.Should().Contain("az ad app create --display-name \"$ACCESS_APP_NAME\"");
        s.Should().Contain("az ad app credential reset").And.Contain("--append");
        s.Should().Contain("MIIBfakefakefake");
        s.Should().NotContain("PRIVATE KEY");
        s.Should().NotContain("admin-consent"); // consent belongs to the Permissions step
    }

    [Fact]
    public void AccessScript_RolesGrantListing_AndAreUpdatedInPlaceOnReRun()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());

        // Container listing is a control-plane Action even over the data plane; account listing is
        // subscription-wide metadata. Both read-only, neither touches keys.
        s.Should().Contain("\\\"Actions\\\": [\\\"Microsoft.Storage/storageAccounts/blobServices/containers/read\\\"]");
        s.Should().Contain("\\\"Microsoft.Storage/storageAccounts/read\\\"");
        s.Should().NotContain("listKeys");
        // `update` takes the `list` shape (roleName/permissions), not the create shape — the existing
        // definition is patched. Resending the create shape fails with KeyError: 'roleName'.
        s.Should().Contain("az role definition update --role-definition \"$(jq -n --argjson e \"$existing\" --argjson d \"$2\"");
        s.Should().Contain(".permissions = [{actions: $d.Actions");
    }

    [Fact]
    public void AccessScript_ReadsTheSignedInSubscription_NothingToTypeUpFront()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());

        s.Should().Contain("SUBSCRIPTION_ID=$(az account show --query id -o tsv)");
        s.Should().NotContain("{{"); // no unfilled placeholders
    }

    [Fact]
    public void AccessScript_StorageScope_DefaultsToWholeSubscription_AndIsEditableInline()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());

        s.Should().Contain("STORAGE_SCOPE=\"\"");
        s.Should().Contain("STORAGE_SCOPE=\"/subscriptions/$SUBSCRIPTION_ID\"");
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
        foreach (string key in new[] { "tenantId=", "subscriptionId=", "accessAppClientId=", "certificateThumbprint=" })
            s.Should().Contain(key);
    }

    [Fact]
    public void Scripts_ReadTheThumbprintBeforeDeletingTheCertificateFile()
    {
        // The thumbprint is what lets the page refuse to keep a certificate other than the one the
        // script registered; it has to be taken while the file still exists.
        foreach (string s in new[]
                 {
                     AzureCloudShellSetup.GenerateAccessScript(AccessInput()),
                     AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput()),
                 })
        {
            int thumb = s.IndexOf("CERT_THUMBPRINT=$(openssl x509 -in \"$CERT_FILE\"", StringComparison.Ordinal);
            int remove = s.IndexOf("rm -f \"$CERT_FILE\"", StringComparison.Ordinal);
            thumb.Should().BeGreaterThan(0);
            remove.Should().BeGreaterThan(thumb);
        }
    }

    [Theory]
    [InlineData("ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01", "ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01", "ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("", null)]
    [InlineData("not-a-thumbprint", null)]
    public void ParseAccessResult_ReadsTheThumbprint_NormalisedOrNone(string printed, string? expected)
    {
        string block = AzureCloudShellSetup.AccessBeginMarker
            + $"\ntenantId={Tenant}\nsubscriptionId={Sub}\naccessAppClientId={AccessId}\ncertificateThumbprint={printed}\n"
            + AzureCloudShellSetup.AccessEndMarker;

        AzureCloudShellSetup.ParseAccessResult(block)!.CertificateThumbprint.Should().Be(expected);
    }

    [Fact]
    public void ParseAccessResult_OlderPasteWithoutThumbprint_StillParses() =>
        AzureCloudShellSetup.ParseAccessResult(AccessBlock())!.CertificateThumbprint.Should().BeNull();

    // ---- Access step on an Azure host: the managed identity ----

    private const string MiPrincipal = "55555555-5555-5555-5555-555555555555";
    private const string MiClient = "66666666-6666-6666-6666-666666666666";

    private static AzureManagedIdentityAccessSetupInput ManagedIdentityInput(bool userAssigned = false) =>
        new(MiPrincipal, MiClient, userAssigned);

    [Fact]
    public void ManagedIdentityAccessScript_AssignsByObjectId_AndNeverRegistersAnApp()
    {
        string s = AzureCloudShellSetup.GenerateManagedIdentityAccessScript(ManagedIdentityInput());

        s.Should().Contain($"MI_PRINCIPAL_ID='{MiPrincipal}'");
        s.Should().Contain("--assignee-object-id \"$MI_PRINCIPAL_ID\" --assignee-principal-type ServicePrincipal");
        s.Should().Contain(AzureCloudShellSetup.BlobDataRoleName).And.Contain(AzureCloudShellSetup.RbacReadRoleName);
        // No app registration, no certificate: that is the whole point of this path.
        s.Should().NotContain("az ad app").And.NotContain("CERT_FILE").And.NotContain("BEGIN CERTIFICATE");
        s.Should().Contain("MI_KIND='system-assigned'");
        AzureCloudShellSetup.GenerateManagedIdentityAccessScript(ManagedIdentityInput(userAssigned: true))
            .Should().Contain("MI_KIND='user-assigned'");
    }

    [Fact]
    public void ManagedIdentityAccessScript_PrintsTheKeys_ParseReads()
    {
        string s = AzureCloudShellSetup.GenerateManagedIdentityAccessScript(ManagedIdentityInput());
        foreach (string key in new[] { "tenantId=", "subscriptionId=", "managedIdentityPrincipalId=", "managedIdentityClientId=", "managedIdentityKind=" })
            s.Should().Contain(key);
        s.Should().Contain(AzureCloudShellSetup.ManagedIdentityBeginMarker).And.Contain(AzureCloudShellSetup.ManagedIdentityEndMarker);
    }

    [Fact]
    public void ManagedIdentityAccessScript_RunsInASubshell_LikeTheOthers()
    {
        string s = AzureCloudShellSetup.GenerateManagedIdentityAccessScript(ManagedIdentityInput()).Replace("\r\n", "\n");
        s.Should().Contain("\n(\nset -euo pipefail\ntrap 'echo; echo \"Connapse setup failed at: $BASH_COMMAND\" >&2' ERR");
        s.Should().Contain(") || echo \"----- CONNAPSE SETUP FAILED");
    }

    private static string ManagedIdentityBlock(string principal = MiPrincipal, string kind = "system-assigned") =>
        AzureCloudShellSetup.ManagedIdentityBeginMarker
        + $"\ntenantId={Tenant}\nsubscriptionId={Sub}\nmanagedIdentityPrincipalId={principal}\nmanagedIdentityClientId={MiClient}\nmanagedIdentityKind={kind}\n"
        + AzureCloudShellSetup.ManagedIdentityEndMarker;

    [Fact]
    public void ParseManagedIdentityAccessResult_WellFormed_ReturnsEverything()
    {
        AzureManagedIdentityAccessResult? r = AzureCloudShellSetup.ParseManagedIdentityAccessResult(ManagedIdentityBlock());

        r.Should().NotBeNull();
        r!.TenantId.Should().Be(Tenant);
        r.SubscriptionId.Should().Be(Sub);
        r.PrincipalId.Should().Be(MiPrincipal);
        r.ClientId.Should().Be(MiClient);
        r.IsUserAssigned.Should().BeFalse();
        AzureCloudShellSetup.ParseManagedIdentityAccessResult(ManagedIdentityBlock(kind: "user-assigned"))!.IsUserAssigned.Should().BeTrue();
    }

    [Fact]
    public void ParseManagedIdentityAccessResult_NonGuid_ReturnsNull() =>
        AzureCloudShellSetup.ParseManagedIdentityAccessResult(ManagedIdentityBlock(principal: "bad")).Should().BeNull();

    [Fact]
    public void ParseManagedIdentityAccessResult_DoesNotReadTheCertificateAppBlock() =>
        AzureCloudShellSetup.ParseManagedIdentityAccessResult(AccessBlock()).Should().BeNull();

    [Fact]
    public void PermissionsScript_ForAManagedIdentity_GrantsAppRolesByRest_AndNeverConsentsAnApp()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(
            PermissionsInput() with { AccessManagedIdentityPrincipalId = MiPrincipal });

        s.Should().Contain($"ACCESS_MI_PRINCIPAL_ID='{MiPrincipal}'");
        s.Should().Contain("/servicePrincipals/$ACCESS_MI_PRINCIPAL_ID/appRoleAssignments");
        s.Should().Contain("\\\"principalId\\\":\\\"$ACCESS_MI_PRINCIPAL_ID\\\"")
            .And.Contain("\\\"resourceId\\\":\\\"$GRAPH_SP_ID\\\"")
            .And.Contain("\\\"appRoleId\\\":\\\"$1\\\"");
        s.Should().NotContain("az ad app permission add").And.NotContain("admin-consent");
        s.Should().Contain("CONSENT_URL=\"\"");
        // The sign-in app is still a certificate app: people cannot sign in through a managed identity.
        s.Should().Contain("register_cert \"$SIGNIN_APP_ID\"");
    }

    [Fact]
    public void PermissionsScript_ForAnApp_StillAddsPermissionsAndConsents()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());

        s.Should().Contain("az ad app permission add").And.Contain("admin-consent");
        s.Should().NotContain("appRoleAssignments");
        s.Should().NotContain("{{graphGrant}}");
    }

    [Fact]
    public void GraphAppRoleGrantCommands_NameTheIdentity_AndBothRoles()
    {
        string s = AzureCloudShellSetup.GraphAppRoleGrantCommands(MiPrincipal);

        s.Should().Contain($"MI='{MiPrincipal}'");
        s.Should().Contain("User.Read.All GroupMember.Read.All");
        s.Should().Contain("appRoleAssignments");
        s.Should().NotContain("{{");
    }

    [Fact]
    public void ParsePermissionsResult_ReadsTheThumbprint()
    {
        string block = AzureCloudShellSetup.PermissionsBeginMarker
            + $"\nsignInAppClientId={SignInId}\nconsentGranted=true\nconsentUrl=\ncertificateThumbprint=abcdef0123456789abcdef0123456789abcdef01\n"
            + AzureCloudShellSetup.PermissionsEndMarker;

        AzureCloudShellSetup.ParsePermissionsResult(block)!.CertificateThumbprint
            .Should().Be("ABCDEF0123456789ABCDEF0123456789ABCDEF01");
    }

    // ---- Per-user permissions step ----

    private static AzurePermissionsSetupInput PermissionsInput() =>
        new(AccessId,
            "https://connapse.example.com/api/v1/auth/cloud/azure/callback",
            "https://connapse.example.com/admin/providers/azure",
            "Connapse-Azure-SignIn",
            Cert);

    [Fact]
    public void PermissionsScript_ContainsSignInAppGraphPermsConsentAndMarkers()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());

        s.Should().Contain(AzureCloudShellSetup.PermissionsBeginMarker).And.Contain(AzureCloudShellSetup.PermissionsEndMarker);
        s.Should().Contain($"ACCESS_APP_ID='{AccessId}'");
        s.Should().Contain("az ad app update --id \"$SIGNIN_APP_ID\" --web-redirect-uris \"$REDIRECT_URI\"");
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
    public void PermissionsScript_RegistersConsentLandingOnAccessApp_AndPassesItAsRedirectUri()
    {
        // The v1 adminconsent endpoint needs a registered reply URL on the app being consented to,
        // else it fails with AADSTS500113 before showing the prompt.
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());

        s.Should().Contain("az ad app update --id \"$ACCESS_APP_ID\" --web-redirect-uris \"$CONSENT_REDIRECT_URI\"");
        s.Should().Contain("CONSENT_REDIRECT_URI='https://connapse.example.com/admin/providers/azure'");
        s.Should().Contain("&redirect_uri=https%3A%2F%2Fconnapse.example.com%2Fadmin%2Fproviders%2Fazure");
    }

    [Fact]
    public void PermissionsScript_PreConsentsSignInAppForEveryone_WhenAdminConsentSucceeds()
    {
        string s = AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput());
        s.Should().Contain("az ad app permission grant --id \"$SIGNIN_APP_ID\" --api \"$GRAPH_API\" --scope \"openid profile offline_access\"");
    }

    [Fact]
    public void Scripts_RunInASubshell_SoAPasteIntoTheInteractiveShellSurvivesAnError()
    {
        // `set -e` pasted into an interactive shell closes the session on the first failure, taking
        // the error message with it. Both scripts wrap the body and print what failed.
        foreach (string script in new[]
                 {
                     AzureCloudShellSetup.GenerateAccessScript(AccessInput()),
                     AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput()),
                 })
        {
            // The template is a raw string literal, so it carries the source file's line endings;
            // a CRLF checkout must not fail an assertion about the script's structure.
            string s = script.Replace("\r\n", "\n");
            s.Should().Contain("\n(\nset -euo pipefail\ntrap 'echo; echo \"Connapse setup failed at: $BASH_COMMAND\" >&2' ERR");
            s.Should().Contain(") || echo \"----- CONNAPSE SETUP FAILED");
        }
    }

    [Fact]
    public void Scripts_NeverDiscoverAppsByDisplayName()
    {
        // A display name is not unique or authoritative: a look-alike registered by another tenant
        // user must never be handed Connapse's roles and Graph permissions.
        AzureCloudShellSetup.GenerateAccessScript(AccessInput()).Should().NotContain("--display-name \"$ACCESS_APP_NAME\" --query '[0]");
        AzureCloudShellSetup.GenerateAccessScript(AccessInput()).Should().NotContain("az ad app list");
        AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput()).Should().NotContain("az ad app list");
    }

    [Fact]
    public void AccessScript_FirstRun_CreatesAFreshApp_ReRunReusesOnlyTheRecordedId()
    {
        string fresh = AzureCloudShellSetup.GenerateAccessScript(AccessInput());
        fresh.Should().Contain("ACCESS_APP_ID=''");
        fresh.Should().Contain("az ad app create --display-name \"$ACCESS_APP_NAME\"");

        string rerun = AzureCloudShellSetup.GenerateAccessScript(AccessInput() with { ExistingAccessAppClientId = AccessId });
        rerun.Should().Contain($"ACCESS_APP_ID='{AccessId}'");
        rerun.Should().Contain("az ad app show --id \"$ACCESS_APP_ID\"");

        // Anything that is not a GUID is treated as "nothing recorded", so it cannot inject shell.
        AzureCloudShellSetup.GenerateAccessScript(AccessInput() with { ExistingAccessAppClientId = "'; rm -rf / #" })
            .Should().Contain("ACCESS_APP_ID=''");
    }

    [Fact]
    public void PermissionsScript_ReRunReusesOnlyTheRecordedSignInId()
    {
        AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput()).Should().Contain("SIGNIN_APP_ID=''");
        AzureCloudShellSetup.GeneratePermissionsScript(PermissionsInput() with { ExistingSignInAppClientId = SignInId })
            .Should().Contain($"SIGNIN_APP_ID='{SignInId}'").And.Contain("az ad app show --id \"$SIGNIN_APP_ID\"");
    }

    [Fact]
    public void AccessScript_RoleAssignmentFailsLoudly_InsteadOfPrintingAPasteBlock()
    {
        string s = AzureCloudShellSetup.GenerateAccessScript(AccessInput());
        s.Should().Contain("assign_role '" + AzureCloudShellSetup.BlobDataRoleName + "' \"$STORAGE_SCOPE\"");
        s.Should().Contain("after 5 attempts");
        s.Should().NotContain("&& break || sleep");
    }

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
