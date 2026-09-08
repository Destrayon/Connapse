namespace Connapse.Core.Utilities;

/// <summary>Inputs the operator supplies (or Connapse derives) before generating the Azure setup script.</summary>
/// <param name="SubscriptionId">The subscription the access identity is scoped to.</param>
/// <param name="StorageScope">A storage-account resource id to scope the blob-data role to, or null/empty
/// for subscription-wide.</param>
/// <param name="RedirectUri">The sign-in app's redirect URI (Connapse's <c>/azure/callback</c>).</param>
/// <param name="AccessAppName">Display name for the access app registration.</param>
/// <param name="SignInAppName">Display name for the sign-in app registration.</param>
/// <param name="PublicCertificatePem">The PUBLIC certificate to upload to both apps. The private key
/// stays on the Connapse host and never appears in the script.</param>
public sealed record AzureSetupInput(
    string SubscriptionId,
    string? StorageScope,
    string RedirectUri,
    string AccessAppName,
    string SignInAppName,
    string PublicCertificatePem);

/// <summary>The non-secret identifiers a completed Azure setup returns.</summary>
public sealed record AzureSetupResult(
    string TenantId,
    string SubscriptionId,
    string AccessAppClientId,
    string SignInAppClientId,
    bool ConsentGranted,
    string? ConsentUrl);

/// <summary>
/// Generates the Azure Cloud Shell (<c>az</c>) script that provisions Connapse's Azure access — two
/// least-privilege custom roles, a certificate-authenticated access app, and an OIDC sign-in app — and
/// parses the block it prints back. A pure string utility, mirroring <see cref="AwsRolesAnywhereSetup"/>.
/// Only the PUBLIC certificate travels in the script; admin consent for the access app's Graph
/// application permissions is attempted and, when the operator lacks the role, deferred to a URL.
/// </summary>
public static class AzureCloudShellSetup
{
    public const string BeginMarker = "----- BEGIN CONNAPSE AZURE SETUP -----";
    public const string EndMarker = "----- END CONNAPSE AZURE SETUP -----";

    /// <summary>The Microsoft Graph resource app id — stable across tenants.</summary>
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public const string BlobDataRoleName = "Connapse Blob Data + Tags Reader";
    public const string RbacReadRoleName = "Connapse RBAC Authorization Reader";

    /// <summary>
    /// Parses the block the script prints. Anchors on the LAST marker pair so pasting the whole terminal
    /// (which echoes the script) still reads the printed output rather than the source. Returns null
    /// unless the four ids are present and well-formed GUIDs.
    /// </summary>
    public static AzureSetupResult? ParseResult(string? pasted)
    {
        if (string.IsNullOrEmpty(pasted)) return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0) return null;
        int start = pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);
        if (start < 0) return null;

        string inner = pasted.Substring(start + BeginMarker.Length, end - start - BeginMarker.Length);

        string? tenant = null, subscription = null, accessId = null, signInId = null,
            consentGranted = null, consentUrl = null;
        foreach (string line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "tenantId": tenant = value; break;
                case "subscriptionId": subscription = value; break;
                case "accessAppClientId": accessId = value; break;
                case "signInAppClientId": signInId = value; break;
                case "consentGranted": consentGranted = value; break;
                case "consentUrl": consentUrl = value; break;
            }
        }

        // The four ids are the load-bearing values; all must be real GUIDs or the paste is malformed.
        if (!Guid.TryParse(tenant, out _) || !Guid.TryParse(subscription, out _)
            || !Guid.TryParse(accessId, out _) || !Guid.TryParse(signInId, out _))
            return null;

        return new AzureSetupResult(
            tenant!, subscription!, accessId!, signInId!,
            ConsentGranted: string.Equals(consentGranted, "true", StringComparison.OrdinalIgnoreCase),
            ConsentUrl: string.IsNullOrWhiteSpace(consentUrl) ? null : consentUrl);
    }

    /// <summary>
    /// The Cloud Shell script. Idempotent where it can be: custom roles are created only if absent, and
    /// app/SP/permission steps tolerate re-runs. Prints a delimited block for Connapse to parse.
    /// </summary>
    public static string GenerateScript(AzureSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string cert = input.PublicCertificatePem.Replace("\r\n", "\n").Trim();
        string storageScope = string.IsNullOrWhiteSpace(input.StorageScope)
            ? "/subscriptions/" + input.SubscriptionId
            : input.StorageScope!.Trim();

        return Template
            .Replace("{{subscription}}", Shell(input.SubscriptionId))
            .Replace("{{storageScope}}", Shell(storageScope))
            .Replace("{{redirectUri}}", Shell(input.RedirectUri))
            .Replace("{{accessAppName}}", Shell(input.AccessAppName))
            .Replace("{{signInAppName}}", Shell(input.SignInAppName))
            .Replace("{{graphAppId}}", GraphAppId)
            .Replace("{{blobRoleName}}", BlobDataRoleName)
            .Replace("{{rbacRoleName}}", RbacReadRoleName)
            .Replace("{{beginMarker}}", BeginMarker)
            .Replace("{{endMarker}}", EndMarker)
            .Replace("{{cert}}", cert);
    }

    /// <summary>Escapes a value for safe inclusion inside single quotes in the generated bash.</summary>
    private static string Shell(string value) => value.Replace("'", "'\\''");

    private const string Template = """
        #!/usr/bin/env bash
        # Connapse Azure setup — run this in Azure Cloud Shell (Bash). It provisions two app
        # registrations and two least-privilege custom roles, then prints a block to paste back into
        # Connapse. Safe to re-run. Only the PUBLIC certificate is here; Connapse keeps the private key.
        set -euo pipefail

        SUBSCRIPTION_ID='{{subscription}}'
        STORAGE_SCOPE='{{storageScope}}'
        REDIRECT_URI='{{redirectUri}}'
        ACCESS_APP_NAME='{{accessAppName}}'
        SIGNIN_APP_NAME='{{signInAppName}}'
        GRAPH_API='{{graphAppId}}'

        az account set --subscription "$SUBSCRIPTION_ID"
        TENANT_ID=$(az account show --query tenantId -o tsv)

        # Public certificate → a temp file for upload (removed at the end).
        CERT_FILE=$(mktemp)
        cat > "$CERT_FILE" <<'CONNAPSE_CERT_EOF'
        {{cert}}
        CONNAPSE_CERT_EOF

        # --- Least-privilege custom roles (create only if absent) ---
        if [ -z "$(az role definition list --name '{{blobRoleName}}' --query '[0].id' -o tsv 2>/dev/null)" ]; then
          az role definition create --role-definition "{
            \"Name\": \"{{blobRoleName}}\", \"IsCustom\": true,
            \"Description\": \"Read blob content and blob index tags for Connapse.\",
            \"Actions\": [], \"NotActions\": [], \"NotDataActions\": [],
            \"DataActions\": [
              \"Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read\",
              \"Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read\"
            ],
            \"AssignableScopes\": [\"/subscriptions/$SUBSCRIPTION_ID\"]
          }" >/dev/null
        fi
        if [ -z "$(az role definition list --name '{{rbacRoleName}}' --query '[0].id' -o tsv 2>/dev/null)" ]; then
          az role definition create --role-definition "{
            \"Name\": \"{{rbacRoleName}}\", \"IsCustom\": true,
            \"Description\": \"Read role and deny assignments at subscription scope for Connapse.\",
            \"Actions\": [
              \"Microsoft.Authorization/roleAssignments/read\",
              \"Microsoft.Authorization/denyAssignments/read\"
            ],
            \"NotActions\": [], \"DataActions\": [], \"NotDataActions\": [],
            \"AssignableScopes\": [\"/subscriptions/$SUBSCRIPTION_ID\"]
          }" >/dev/null
        fi

        # --- Access app registration (certificate-authenticated) ---
        ACCESS_APP_ID=$(az ad app create --display-name "$ACCESS_APP_NAME" --query appId -o tsv)
        az ad app credential reset --id "$ACCESS_APP_ID" --cert "@$CERT_FILE" --append >/dev/null
        az ad sp create --id "$ACCESS_APP_ID" >/dev/null 2>&1 || true

        # Roles wait for the custom-role definitions + SP to propagate; retry briefly.
        for i in 1 2 3 4 5; do
          az role assignment create --assignee "$ACCESS_APP_ID" --role '{{blobRoleName}}' --scope "$STORAGE_SCOPE" >/dev/null 2>&1 && break || sleep 10
        done
        for i in 1 2 3 4 5; do
          az role assignment create --assignee "$ACCESS_APP_ID" --role '{{rbacRoleName}}' --scope "/subscriptions/$SUBSCRIPTION_ID" >/dev/null 2>&1 && break || sleep 10
        done

        # Microsoft Graph application permissions (resolve the app-role ids live).
        USER_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='User.Read.All'].id | [0]" -o tsv)
        GROUP_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$USER_READ_ALL=Role" >/dev/null
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$GROUP_READ_ALL=Role" >/dev/null

        # --- Sign-in app registration (OIDC, certificate client-assertion) ---
        SIGNIN_APP_ID=$(az ad app create --display-name "$SIGNIN_APP_NAME" --web-redirect-uris "$REDIRECT_URI" --query appId -o tsv)
        az ad app credential reset --id "$SIGNIN_APP_ID" --cert "@$CERT_FILE" --append >/dev/null
        az ad sp create --id "$SIGNIN_APP_ID" >/dev/null 2>&1 || true

        rm -f "$CERT_FILE"

        # --- Admin consent (Graph application permissions — Global Admin / Privileged Role Admin only) ---
        CONSENT_GRANTED=false
        if az ad app permission admin-consent --id "$ACCESS_APP_ID" >/dev/null 2>&1; then
          CONSENT_GRANTED=true
        fi
        CONSENT_URL="https://login.microsoftonline.com/$TENANT_ID/adminconsent?client_id=$ACCESS_APP_ID"

        echo
        printf '%s\ntenantId=%s\nsubscriptionId=%s\naccessAppClientId=%s\nsignInAppClientId=%s\nconsentGranted=%s\nconsentUrl=%s\n%s\n' \
          "{{beginMarker}}" "$TENANT_ID" "$SUBSCRIPTION_ID" "$ACCESS_APP_ID" "$SIGNIN_APP_ID" "$CONSENT_GRANTED" "$CONSENT_URL" "{{endMarker}}"
        """;
}
