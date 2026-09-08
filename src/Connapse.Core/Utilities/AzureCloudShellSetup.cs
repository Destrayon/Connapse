namespace Connapse.Core.Utilities;

/// <summary>Inputs for the Access step's script: the subscription and storage scope the access identity
/// is granted on, and the PUBLIC certificate it authenticates with (the private key stays on the host).</summary>
public sealed record AzureAccessSetupInput(
    string SubscriptionId,
    string? StorageScope,
    string AccessAppName,
    string PublicCertificatePem);

/// <summary>The non-secret identifiers the Access script prints back.</summary>
public sealed record AzureAccessResult(string TenantId, string SubscriptionId, string AccessAppClientId);

/// <summary>Inputs for the Per-user permissions step's script: which access app to grant Graph
/// permissions on, the sign-in app's redirect URI, and the PUBLIC certificate for the sign-in app.</summary>
public sealed record AzurePermissionsSetupInput(
    string AccessAppClientId,
    string RedirectUri,
    string SignInAppName,
    string PublicCertificatePem);

/// <summary>What the Per-user permissions script prints back, including whether the operator was able
/// to grant admin consent (and the URL to hand an administrator when they were not).</summary>
public sealed record AzurePermissionsResult(string SignInAppClientId, bool ConsentGranted, string? ConsentUrl);

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
        string storageScope = string.IsNullOrWhiteSpace(input.StorageScope)
            ? "/subscriptions/" + input.SubscriptionId
            : input.StorageScope!.Trim();

        return AccessTemplate
            .Replace("{{subscription}}", Shell(input.SubscriptionId))
            .Replace("{{storageScope}}", Shell(storageScope))
            .Replace("{{accessAppName}}", Shell(input.AccessAppName))
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

        return new AzureAccessResult(tenant!, subscription!, accessId!);
    }

    // ---- Per-user permissions step ----

    public static string GeneratePermissionsScript(AzurePermissionsSetupInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PermissionsTemplate
            .Replace("{{accessAppId}}", Shell(input.AccessAppClientId))
            .Replace("{{redirectUri}}", Shell(input.RedirectUri))
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

        return new AzurePermissionsResult(
            signInId!,
            ConsentGranted: string.Equals(consent, "true", StringComparison.OrdinalIgnoreCase),
            ConsentUrl: string.IsNullOrWhiteSpace(url) ? null : url);
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

    private const string AccessTemplate = """
        #!/usr/bin/env bash
        # Connapse Azure setup — step 1 of 2 (Access). Run in Azure Cloud Shell (Bash). Creates two
        # least-privilege custom roles and Connapse's certificate-authenticated access app, then prints
        # a block to paste back into Connapse. Safe to re-run. Only the PUBLIC certificate is here.
        set -euo pipefail

        SUBSCRIPTION_ID='{{subscription}}'
        STORAGE_SCOPE='{{storageScope}}'
        ACCESS_APP_NAME='{{accessAppName}}'

        az account set --subscription "$SUBSCRIPTION_ID"
        TENANT_ID=$(az account show --query tenantId -o tsv)

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
        rm -f "$CERT_FILE"

        # Role assignments wait for the role definitions + service principal to propagate; retry briefly.
        for i in 1 2 3 4 5; do
          az role assignment create --assignee "$ACCESS_APP_ID" --role '{{blobRoleName}}' --scope "$STORAGE_SCOPE" >/dev/null 2>&1 && break || sleep 10
        done
        for i in 1 2 3 4 5; do
          az role assignment create --assignee "$ACCESS_APP_ID" --role '{{rbacRoleName}}' --scope "/subscriptions/$SUBSCRIPTION_ID" >/dev/null 2>&1 && break || sleep 10
        done

        echo
        printf '%s\ntenantId=%s\nsubscriptionId=%s\naccessAppClientId=%s\n%s\n' \
          "{{beginMarker}}" "$TENANT_ID" "$SUBSCRIPTION_ID" "$ACCESS_APP_ID" "{{endMarker}}"
        """;

    private const string PermissionsTemplate = """
        #!/usr/bin/env bash
        # Connapse Azure setup — step 2 of 2 (Per-user permissions). Run in Azure Cloud Shell (Bash).
        # Creates the sign-in app people authenticate through and grants the access app the Microsoft
        # Graph permissions it reads the directory with. Admin consent needs a Global Administrator or
        # Privileged Role Administrator; if you are not one, the script prints a link to send them.
        set -euo pipefail

        ACCESS_APP_ID='{{accessAppId}}'
        REDIRECT_URI='{{redirectUri}}'
        SIGNIN_APP_NAME='{{signInAppName}}'
        GRAPH_API='{{graphAppId}}'

        TENANT_ID=$(az account show --query tenantId -o tsv)

        CERT_FILE=$(mktemp)
        cat > "$CERT_FILE" <<'CONNAPSE_CERT_EOF'
        {{cert}}
        CONNAPSE_CERT_EOF

        # --- Sign-in app registration (OIDC, certificate client-assertion) ---
        SIGNIN_APP_ID=$(az ad app create --display-name "$SIGNIN_APP_NAME" --web-redirect-uris "$REDIRECT_URI" --query appId -o tsv)
        az ad app credential reset --id "$SIGNIN_APP_ID" --cert "@$CERT_FILE" --append >/dev/null
        az ad sp create --id "$SIGNIN_APP_ID" >/dev/null 2>&1 || true
        rm -f "$CERT_FILE"

        # --- Microsoft Graph application permissions on the ACCESS app (ids resolved live) ---
        USER_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='User.Read.All'].id | [0]" -o tsv)
        GROUP_READ_ALL=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='GroupMember.Read.All'].id | [0]" -o tsv)
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$USER_READ_ALL=Role" >/dev/null
        az ad app permission add --id "$ACCESS_APP_ID" --api "$GRAPH_API" --api-permissions "$GROUP_READ_ALL=Role" >/dev/null

        # --- Admin consent (Global Administrator / Privileged Role Administrator only) ---
        CONSENT_GRANTED=false
        if az ad app permission admin-consent --id "$ACCESS_APP_ID" >/dev/null 2>&1; then
          CONSENT_GRANTED=true
        fi
        CONSENT_URL="https://login.microsoftonline.com/$TENANT_ID/adminconsent?client_id=$ACCESS_APP_ID"

        echo
        printf '%s\nsignInAppClientId=%s\nconsentGranted=%s\nconsentUrl=%s\n%s\n' \
          "{{beginMarker}}" "$SIGNIN_APP_ID" "$CONSENT_GRANTED" "$CONSENT_URL" "{{endMarker}}"
        """;
}
