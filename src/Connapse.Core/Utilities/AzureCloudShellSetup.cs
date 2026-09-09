namespace Connapse.Core.Utilities;

/// <summary>Inputs for the Access step's script: the app name and the PUBLIC certificate it authenticates
/// with (the private key stays on the host). The subscription is whichever one Cloud Shell is signed
/// in to — the script reads it and prints it back, so the operator types nothing.</summary>
/// <param name="ExistingAccessAppClientId">The access app Connapse already recorded, when re-running;
/// the script reuses exactly that app and never looks one up by display name (names are neither
/// unique nor authoritative, so a pre-registered look-alike could otherwise be granted the roles).</param>
public sealed record AzureAccessSetupInput(
    string AccessAppName,
    string PublicCertificatePem,
    string? ExistingAccessAppClientId = null);

/// <summary>The non-secret identifiers the Access script prints back.</summary>
/// <param name="CertificateThumbprint">SHA-1 thumbprint (upper-case hex, no separators) of the certificate
/// the script registered, so the page can refuse to promote a different one; null when the paste
/// predates the script printing it.</param>
public sealed record AzureAccessResult(
    string TenantId, string SubscriptionId, string AccessAppClientId, string? CertificateThumbprint = null);

/// <summary>Inputs for the Per-user permissions step's script: which access app to grant Graph
/// permissions on, the sign-in app's redirect URI, the page Entra should bounce back to after admin
/// consent (registered on the access app — without one, the consent endpoint refuses with
/// AADSTS500113), and the PUBLIC certificate for the sign-in app.</summary>
public sealed record AzurePermissionsSetupInput(
    string AccessAppClientId,
    string RedirectUri,
    string ConsentRedirectUri,
    string SignInAppName,
    string PublicCertificatePem,
    string? ExistingSignInAppClientId = null);

/// <summary>What the Per-user permissions script prints back, including whether the operator was able
/// to grant admin consent (and the URL to hand an administrator when they were not).</summary>
public sealed record AzurePermissionsResult(
    string SignInAppClientId, bool ConsentGranted, string? ConsentUrl, string? CertificateThumbprint = null);

/// <summary>
/// Generates the Azure Cloud Shell (<c>az</c>) scripts that provision Connapse's Azure setup, one per
/// step, and parses the block each prints back. A pure string utility mirroring
/// <see cref="AwsRolesAnywhereSetup"/>: the Access script creates two least-privilege custom roles and
/// a certificate-authenticated access app; the Permissions script creates the OIDC sign-in app and
/// adds the access app's Microsoft Graph application permissions, attempting admin consent. Only the
/// PUBLIC certificate ever appears in a script.
/// </summary>
public static class AzureCloudShellSetup
{
    public const string AccessBeginMarker = "----- BEGIN CONNAPSE AZURE ACCESS -----";
    public const string AccessEndMarker = "----- END CONNAPSE AZURE ACCESS -----";
    public const string PermissionsBeginMarker = "----- BEGIN CONNAPSE AZURE PERMISSIONS -----";
    public const string PermissionsEndMarker = "----- END CONNAPSE AZURE PERMISSIONS -----";

    /// <summary>The Microsoft Graph resource app id — stable across tenants.</summary>
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public const string BlobDataRoleName = "Connapse Blob Data + Tags Reader";
    public const string RbacReadRoleName = "Connapse RBAC Authorization Reader";

    /// <summary>What the person running the Access script needs (Azure RBAC + Entra), not the runtime identity.</summary>
    public static readonly IReadOnlyList<string> AccessScriptRequirements =
    [
        "Owner or User Access Administrator on the subscription (to create the two custom roles and assign them)",
        "permission to register applications (the tenant default, or Application Developer / Application Administrator)",
    ];

    /// <summary>What the person running the Permissions script needs; consent is the one step that may need someone else.</summary>
    public static readonly IReadOnlyList<string> PermissionsScriptRequirements =
    [
        "permission to register applications",
        "Global Administrator or Privileged Role Administrator to grant admin consent (otherwise the script prints a consent link for one)",
    ];

    // ---- Access step ----

    public static string GenerateAccessScript(AzureAccessSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return AccessTemplate
            .Replace("{{accessAppName}}", Shell(input.AccessAppName))
            .Replace("{{existingAccessAppId}}", Shell(GuidOrEmpty(input.ExistingAccessAppClientId)))
            .Replace("{{blobRoleName}}", BlobDataRoleName)
            .Replace("{{rbacRoleName}}", RbacReadRoleName)
            .Replace("{{beginMarker}}", AccessBeginMarker)
            .Replace("{{endMarker}}", AccessEndMarker)
            .Replace("{{cert}}", input.PublicCertificatePem.Replace("\r\n", "\n").Trim());
    }

    /// <summary>Parses the Access block. Null unless all three ids are present, well-formed GUIDs.</summary>
    public static AzureAccessResult? ParseAccessResult(string? pasted)
    {
        var values = ExtractBlock(pasted, AccessBeginMarker, AccessEndMarker);
        if (values is null) return null;

        values.TryGetValue("tenantId", out string? tenant);
        values.TryGetValue("subscriptionId", out string? subscription);
        values.TryGetValue("accessAppClientId", out string? accessId);

        if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(subscription, out _) || !Guid.TryParse(accessId, out _))
            return null;

        values.TryGetValue("certificateThumbprint", out string? thumbprint);
        return new AzureAccessResult(tenant!, subscription!, accessId!, Thumbprint(thumbprint));
    }

    /// <summary>A thumbprint as the scripts print it: 40 hex digits, upper-cased here; anything else
    /// (an older script that did not print one, or openssl missing) reads as none.</summary>
    private static string? Thumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string cleaned = value.Replace(":", "").Trim().ToUpperInvariant();
        return cleaned.Length == 40 && cleaned.All(Uri.IsHexDigit) ? cleaned : null;
    }

    // ---- Per-user permissions step ----

    public static string GeneratePermissionsScript(AzurePermissionsSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PermissionsTemplate
            .Replace("{{accessAppId}}", Shell(input.AccessAppClientId))
            .Replace("{{redirectUri}}", Shell(input.RedirectUri))
            .Replace("{{consentRedirectUri}}", Shell(input.ConsentRedirectUri))
            .Replace("{{consentRedirectEncoded}}", Uri.EscapeDataString(input.ConsentRedirectUri))
            .Replace("{{existingSignInAppId}}", Shell(GuidOrEmpty(input.ExistingSignInAppClientId)))
            .Replace("{{signInAppName}}", Shell(input.SignInAppName))
            .Replace("{{graphAppId}}", GraphAppId)
            .Replace("{{beginMarker}}", PermissionsBeginMarker)
            .Replace("{{endMarker}}", PermissionsEndMarker)
            .Replace("{{cert}}", input.PublicCertificatePem.Replace("\r\n", "\n").Trim());
    }

    /// <summary>Parses the Permissions block. Null unless the sign-in app id is a well-formed GUID.</summary>
    public static AzurePermissionsResult? ParsePermissionsResult(string? pasted)
    {
        var values = ExtractBlock(pasted, PermissionsBeginMarker, PermissionsEndMarker);
        if (values is null) return null;

        values.TryGetValue("signInAppClientId", out string? signInId);
        if (!Guid.TryParse(signInId, out _)) return null;

        values.TryGetValue("consentGranted", out string? consent);
        values.TryGetValue("consentUrl", out string? url);
        values.TryGetValue("certificateThumbprint", out string? thumbprint);

        return new AzurePermissionsResult(
            signInId!,
            ConsentGranted: string.Equals(consent, "true", StringComparison.OrdinalIgnoreCase),
            ConsentUrl: string.IsNullOrWhiteSpace(url) ? null : url,
            CertificateThumbprint: Thumbprint(thumbprint));
    }

    // ---- shared ----

    /// <summary>
    /// Reads the key=value lines between the LAST marker pair, so pasting the whole terminal (which
    /// echoes the script) still reads the printed output rather than the source.
    /// </summary>
    private static Dictionary<string, string>? ExtractBlock(string? pasted, string begin, string end)
    {
        if (string.IsNullOrEmpty(pasted)) return null;

        int endIdx = pasted.LastIndexOf(end, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        int startIdx = pasted.LastIndexOf(begin, endIdx, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        string inner = pasted.Substring(startIdx + begin.Length, endIdx - startIdx - begin.Length);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    /// <summary>Escapes a value for safe inclusion inside single quotes in the generated bash.</summary>
    private static string Shell(string value) => value.Replace("'", "'\\''");

    /// <summary>An app id is only ever a GUID; anything else is treated as "none recorded".</summary>
    private static string GuidOrEmpty(string? value) =>
        Guid.TryParse(value?.Trim(), out Guid id) ? id.ToString() : string.Empty;

    private const string AccessTemplate = """
        #!/usr/bin/env bash
        # Connapse Azure setup — step 1 of 2 (Access). Run in Azure Cloud Shell (Bash). Creates two
        # least-privilege custom roles and Connapse's certificate-authenticated access app, then prints
        # a block to paste back into Connapse. Safe to re-run. Only the PUBLIC certificate is here.
        #
        # It uses the subscription Cloud Shell is signed in to. To use a different one, run
        #   az account set --subscription <id>
        # first. To limit the blob role to one storage account instead of the whole subscription,
        # set STORAGE_SCOPE below to that storage account's resource id.
        #
        # Runs in a subshell so that pasting it into the interactive shell cannot close your
        # session on an error: the failing command is printed and you stay signed in.
        (
        set -euo pipefail
        trap 'echo; echo "Connapse setup failed at: $BASH_COMMAND" >&2' ERR

        ACCESS_APP_NAME='{{accessAppName}}'
        STORAGE_SCOPE=""

        SUBSCRIPTION_ID=$(az account show --query id -o tsv)
        TENANT_ID=$(az account show --query tenantId -o tsv)
        [ -n "$STORAGE_SCOPE" ] || STORAGE_SCOPE="/subscriptions/$SUBSCRIPTION_ID"
        echo "Using subscription $SUBSCRIPTION_ID ($(az account show --query name -o tsv))"

        CERT_FILE=$(mktemp)
        cat > "$CERT_FILE" <<'CONNAPSE_CERT_EOF'
        {{cert}}
        CONNAPSE_CERT_EOF

        # --- Least-privilege custom roles: created, or updated in place so re-runs upgrade them ---
        upsert_role() {  # $1 role name, $2 definition JSON in the shape `az role definition create` takes
          local existing
          existing=$(az role definition list --name "$1" --custom-role-only true -o json 2>/dev/null | jq -c '.[0] // empty')
          if [ -z "$existing" ]; then
            az role definition create --role-definition "$2" >/dev/null
          else
            # `update` wants the shape `list` returns (roleName, permissions[], id), so patch the
            # existing definition rather than resending the create-shaped one.
            az role definition update --role-definition "$(jq -n --argjson e "$existing" --argjson d "$2" '
              $e | .description = $d.Description
                 | .permissions = [{actions: $d.Actions, notActions: $d.NotActions,
                                    dataActions: $d.DataActions, notDataActions: $d.NotDataActions}]
                 | .assignableScopes = $d.AssignableScopes')" >/dev/null
          fi
        }
        # Data-plane blob reads, plus listing containers (a control-plane action even over the
        # data plane) so the connection form can offer a container list.
        upsert_role '{{blobRoleName}}' "{
          \"Name\": \"{{blobRoleName}}\", \"IsCustom\": true,
          \"Description\": \"Read blob content, blob index tags, and container names for Connapse.\",
          \"Actions\": [\"Microsoft.Storage/storageAccounts/blobServices/containers/read\"],
          \"NotActions\": [], \"NotDataActions\": [],
          \"DataActions\": [
            \"Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read\",
            \"Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read\"
          ],
          \"AssignableScopes\": [\"/subscriptions/$SUBSCRIPTION_ID\"]
        }"
        # Subscription-wide reads: role and deny assignments for per-user filtering, and storage
        # account metadata (names, endpoints — never keys) so the form can offer an account list.
        upsert_role '{{rbacRoleName}}' "{
          \"Name\": \"{{rbacRoleName}}\", \"IsCustom\": true,
          \"Description\": \"Read role and deny assignments and storage account metadata at subscription scope for Connapse.\",
          \"Actions\": [
            \"Microsoft.Authorization/roleAssignments/read\",
            \"Microsoft.Authorization/denyAssignments/read\",
            \"Microsoft.Storage/storageAccounts/read\"
          ],
          \"NotActions\": [], \"DataActions\": [], \"NotDataActions\": [],
          \"AssignableScopes\": [\"/subscriptions/$SUBSCRIPTION_ID\"]
        }"

        # --- Access app registration (certificate-authenticated) ---
        # Re-runs reuse exactly the app Connapse recorded, by id. Never by display name: names are
        # not unique, and a look-alike registered by someone else must not be handed these roles.
        ACCESS_APP_ID='{{existingAccessAppId}}'
        if [ -n "$ACCESS_APP_ID" ]; then
          az ad app show --id "$ACCESS_APP_ID" --query appId -o tsv >/dev/null \
            || { echo "The access app Connapse recorded ($ACCESS_APP_ID) no longer exists. Reset the Access step in Connapse and run again." >&2; exit 1; }
        else
          ACCESS_APP_ID=$(az ad app create --display-name "$ACCESS_APP_NAME" --query appId -o tsv)
        fi
        # Registers the certificate; a re-run with the same certificate is fine, anything else is fatal.
        register_cert() {
          local out
          if out=$(az ad app credential reset --id "$1" --cert "@$CERT_FILE" --append 2>&1 >/dev/null); then return 0; fi
          if echo "$out" | grep -qi "exist"; then echo "Certificate already registered on $1."; return 0; fi
          echo "$out" >&2; return 1
        }
        register_cert "$ACCESS_APP_ID"
        az ad sp create --id "$ACCESS_APP_ID" >/dev/null 2>&1 || true
        # Printed back so Connapse can refuse to keep a different certificate than the one registered.
        CERT_THUMBPRINT=$(openssl x509 -in "$CERT_FILE" -noout -fingerprint -sha1 2>/dev/null | sed 's/.*=//; s/://g')
        rm -f "$CERT_FILE"

        # Role assignments wait for the role definitions + service principal to propagate; retry
        # briefly, and fail the script if they never land — a paste block without these grants
        # would record a setup that cannot read anything.
        assign_role() {  # $1 role name, $2 scope
          local i
          for i in 1 2 3 4 5; do
            if az role assignment create --assignee "$ACCESS_APP_ID" --role "$1" --scope "$2" >/dev/null 2>&1; then return 0; fi
            sleep 10
          done
          echo "Could not assign '$1' at $2 after 5 attempts." >&2
          return 1
        }
        assign_role '{{blobRoleName}}' "$STORAGE_SCOPE"
        assign_role '{{rbacRoleName}}' "/subscriptions/$SUBSCRIPTION_ID"

        echo
        printf '%s\ntenantId=%s\nsubscriptionId=%s\naccessAppClientId=%s\ncertificateThumbprint=%s\n%s\n' \
          "{{beginMarker}}" "$TENANT_ID" "$SUBSCRIPTION_ID" "$ACCESS_APP_ID" "$CERT_THUMBPRINT" "{{endMarker}}"
        ) || echo "----- CONNAPSE SETUP FAILED: read the error above. Nothing to paste back yet. -----"
        """;

    private const string PermissionsTemplate = """
        #!/usr/bin/env bash
        # Connapse Azure setup — step 2 of 2 (Per-user permissions). Run in Azure Cloud Shell (Bash).
        # Creates the sign-in app people authenticate through and grants the access app the Microsoft
        # Graph permissions it reads the directory with. Admin consent needs a Global Administrator or
        # Privileged Role Administrator; if you are not one, the script prints a link to send them.
        #
        # Runs in a subshell so that pasting it into the interactive shell cannot close your
        # session on an error: the failing command is printed and you stay signed in.
        (
        set -euo pipefail
        trap 'echo; echo "Connapse setup failed at: $BASH_COMMAND" >&2' ERR

        ACCESS_APP_ID='{{accessAppId}}'
        REDIRECT_URI='{{redirectUri}}'
        CONSENT_REDIRECT_URI='{{consentRedirectUri}}'
        SIGNIN_APP_NAME='{{signInAppName}}'
        GRAPH_API='{{graphAppId}}'

        TENANT_ID=$(az account show --query tenantId -o tsv)

        CERT_FILE=$(mktemp)
        cat > "$CERT_FILE" <<'CONNAPSE_CERT_EOF'
        {{cert}}
        CONNAPSE_CERT_EOF

        # --- Sign-in app registration (OIDC, certificate client-assertion) ---
        # Reused only by the id Connapse recorded, never by display name (see the Access script).
        SIGNIN_APP_ID='{{existingSignInAppId}}'
        if [ -n "$SIGNIN_APP_ID" ]; then
          az ad app show --id "$SIGNIN_APP_ID" --query appId -o tsv >/dev/null \
            || { echo "The sign-in app Connapse recorded ($SIGNIN_APP_ID) no longer exists. Reset the sign-in application in Connapse and run again." >&2; exit 1; }
        else
          SIGNIN_APP_ID=$(az ad app create --display-name "$SIGNIN_APP_NAME" --query appId -o tsv)
        fi
        az ad app update --id "$SIGNIN_APP_ID" --web-redirect-uris "$REDIRECT_URI"
        # Registers the certificate; a re-run with the same certificate is fine, anything else is fatal.
        register_cert() {
          local out
          if out=$(az ad app credential reset --id "$1" --cert "@$CERT_FILE" --append 2>&1 >/dev/null); then return 0; fi
          if echo "$out" | grep -qi "exist"; then echo "Certificate already registered on $1."; return 0; fi
          echo "$out" >&2; return 1
        }
        register_cert "$SIGNIN_APP_ID"
        az ad sp create --id "$SIGNIN_APP_ID" >/dev/null 2>&1 || true
        # Printed back so Connapse can refuse to keep a different certificate than the one registered.
        CERT_THUMBPRINT=$(openssl x509 -in "$CERT_FILE" -noout -fingerprint -sha1 2>/dev/null | sed 's/.*=//; s/://g')
        rm -f "$CERT_FILE"

        # The admin-consent page bounces back to a reply URL registered on the ACCESS app; without one
        # Entra refuses with AADSTS500113. Connapse's Providers page is that landing spot.
        az ad app update --id "$ACCESS_APP_ID" --web-redirect-uris "$CONSENT_REDIRECT_URI"

        # --- Microsoft Graph application permissions on the ACCESS app (ids resolved live) ---
        USER_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='User.Read.All'].id | [0]" -o tsv)
        GROUP_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$USER_READ_ALL=Role" >/dev/null
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$GROUP_READ_ALL=Role" >/dev/null

        # --- Admin consent (Global Administrator / Privileged Role Administrator only) ---
        CONSENT_GRANTED=false
        if az ad app permission admin-consent --id "$ACCESS_APP_ID" >/dev/null 2>&1; then
          CONSENT_GRANTED=true
          # Same administrator can pre-consent the sign-in app's delegated sign-in scopes for everyone,
          # so people never see a consent prompt. offline_access is implied by the code flow;
          # Connapse stores no tokens, only the user's object id and tenant.
          az ad app permission grant --id "$SIGNIN_APP_ID" --api "$GRAPH_API" --scope "openid profile offline_access" >/dev/null 2>&1 || true
        fi
        CONSENT_URL="https://login.microsoftonline.com/$TENANT_ID/adminconsent?client_id=$ACCESS_APP_ID&redirect_uri={{consentRedirectEncoded}}"

        echo
        printf '%s\nsignInAppClientId=%s\nconsentGranted=%s\nconsentUrl=%s\ncertificateThumbprint=%s\n%s\n' \
          "{{beginMarker}}" "$SIGNIN_APP_ID" "$CONSENT_GRANTED" "$CONSENT_URL" "$CERT_THUMBPRINT" "{{endMarker}}"
        ) || echo "----- CONNAPSE SETUP FAILED: read the error above. Nothing to paste back yet. -----"
        """;
}
