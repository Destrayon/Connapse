namespace Connapse.Core.Utilities;

/// <summary>The non-secret identifiers a completed Roles Anywhere setup returns.</summary>
public sealed record AwsRolesAnywhereArns(string TrustAnchorArn, string ProfileArn, string RoleArn, string Region);

/// <summary>
/// Generates the CloudShell script that provisions Connapse's Roles Anywhere access — reusing the shared
/// role/policy/profile and creating this instance's own trust anchor — and parses the ARN block it prints
/// back. A pure string utility, mirroring <see cref="AwsIamUserSetup"/>.
/// </summary>
public static class AwsRolesAnywhereSetup
{
    public const string BeginMarker = "----- BEGIN CONNAPSE AWS ROLE -----";
    public const string EndMarker = "----- END CONNAPSE AWS ROLE -----";

    /// <summary>Shared-resource name prefix. A constant, deliberately not a parameter, so every instance shares them.</summary>
    public const string NamePrefix = "connapse";

    /// <summary>The CloudShell (admin) permissions the setup script needs — not the runtime identity's.</summary>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "iam:GetRole", "iam:ListRoleTags", "iam:CreateRole", "iam:PutRolePolicy",
        "rolesanywhere:ListTrustAnchors", "rolesanywhere:CreateTrustAnchor",
        "rolesanywhere:ListProfiles", "rolesanywhere:CreateProfile",
        "sts:GetCallerIdentity"
    ];

    /// <summary>
    /// Parses the ARN block the script prints. Anchors on the LAST marker pair, so pasting the whole
    /// terminal (which echoes the script) still reads the printed output rather than the source.
    /// </summary>
    public static AwsRolesAnywhereArns? ParseResult(string? pasted)
    {
        if (string.IsNullOrEmpty(pasted)) return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        if (end < 0) return null;
        int start = pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);
        if (start < 0) return null;

        string inner = pasted.Substring(start + BeginMarker.Length, end - start - BeginMarker.Length);

        string? trustAnchor = null, profile = null, role = null, region = null;
        foreach (string line in inner.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq];
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "trustAnchorArn": trustAnchor = value; break;
                case "profileArn": profile = value; break;
                case "roleArn": role = value; break;
                case "region": region = value; break;
            }
        }

        if (string.IsNullOrEmpty(trustAnchor) || string.IsNullOrEmpty(profile)
            || string.IsNullOrEmpty(role) || string.IsNullOrEmpty(region))
            return null;

        return new AwsRolesAnywhereArns(trustAnchor, profile, role, region);
    }

    /// <summary>
    /// The CloudShell script that provisions Roles Anywhere access for this instance. The shared role,
    /// ConnapseRead policy, and profile are reused if a Connapse-tagged copy already exists (so a second
    /// instance does not clobber them); this instance's own trust anchor is created from the embedded cert.
    /// </summary>
    public static string GenerateScript(string certificatePem, string? region)
    {
        string cert = certificatePem.Replace("\r\n", "\n").Trim();
        string pinnedRegion = SanitiseRegion(region);
        string policy = S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n");
        string account = S3SetupPolicy.AccountPlaceholder;
        string role = $"{NamePrefix}-rolesanywhere";
        string profile = $"{NamePrefix}-rolesanywhere";

        string script = $$"""
            # Provisions Connapse's IAM Roles Anywhere access. Safe to re-run and to run from a
            # second Connapse instance: the role, policy, and profile are shared and reused.
            FAILED=""
            REGION="{{pinnedRegion}}"
            ROLE="{{role}}"
            PROFILE="{{profile}}"

            ACCOUNT=$(aws sts get-caller-identity --query Account --output text) || FAILED="could not resolve the AWS account"

            # --- Shared role (reuse the Connapse-tagged one, else create) ---
            ROLE_ARN=""
            if aws iam get-role --role-name "$ROLE" >/dev/null 2>&1; then
              OWNER=$(aws iam list-role-tags --role-name "$ROLE" --query "Tags[?Key=='CreatedBy'].Value" --output text)
              if [ "$OWNER" != "Connapse" ]; then FAILED="a role named $ROLE already exists and Connapse did not create it"; fi
              ROLE_ARN=$(aws iam get-role --role-name "$ROLE" --query 'Role.Arn' --output text)
            else
              TRUST='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"rolesanywhere.amazonaws.com"},"Action":["sts:AssumeRole","sts:SetSourceIdentity"],"Condition":{"ArnLike":{"aws:SourceArn":"arn:aws:rolesanywhere:*:{{account}}:trust-anchor/*"} } }]}'
              TRUST=${TRUST//{{account}}/$ACCOUNT}
              ROLE_ARN=$(aws iam create-role --role-name "$ROLE" --assume-role-policy-document "$TRUST" --tags Key=CreatedBy,Value=Connapse --query 'Role.Arn' --output text) || FAILED="could not create the role"
            fi

            # --- ConnapseRead policy (apply/update on the shared role) ---
            if [ -z "$FAILED" ]; then
              POLICY='{{policy}}'
              POLICY=${POLICY//{{account}}/$ACCOUNT}
              aws iam put-role-policy --role-name "$ROLE" --policy-name ConnapseRead --policy-document "$POLICY" || FAILED="could not apply the ConnapseRead policy"
            fi

            # --- Shared profile (reuse by name, else create) ---
            PROFILE_ARN=""
            if [ -z "$FAILED" ]; then
              PROFILE_ARN=$(aws rolesanywhere list-profiles --query "profiles[?name=='$PROFILE'].profileArn | [0]" --output text)
              if [ "$PROFILE_ARN" = "None" ] || [ -z "$PROFILE_ARN" ]; then
                PROFILE_ARN=$(aws rolesanywhere create-profile --name "$PROFILE" --role-arns "$ROLE_ARN" --enabled --query 'profile.profileArn' --output text) || FAILED="could not create the profile"
              fi
            fi

            # --- Per-instance trust anchor from this instance's certificate ---
            TA_ARN=""
            if [ -z "$FAILED" ]; then
              CERT='{{cert}}'
              FP=$(printf '%s' "$CERT" | openssl x509 -noout -fingerprint -sha256 | sed 's/.*=//; s/://g' | cut -c1-16 | tr 'A-Z' 'a-z')
              TA_NAME="{{NamePrefix}}-ra-$FP"
              TA_ARN=$(aws rolesanywhere list-trust-anchors --query "trustAnchors[?name=='$TA_NAME'].trustAnchorArn | [0]" --output text)
              if [ "$TA_ARN" = "None" ] || [ -z "$TA_ARN" ]; then
                jq -n --arg cert "$CERT" '{sourceData:{x509CertificateData:$cert},sourceType:"CERTIFICATE_BUNDLE"}' > "$HOME/connapse-ta-source.json"
                TA_ARN=$(aws rolesanywhere create-trust-anchor --name "$TA_NAME" --source "file://$HOME/connapse-ta-source.json" --enabled --query 'trustAnchor.trustAnchorArn' --output text) || FAILED="could not create the trust anchor"
                rm -f "$HOME/connapse-ta-source.json"
              fi
            fi

            # --- Report ---
            if [ -z "$FAILED" ]; then
              printf '%s\ntrustAnchorArn=%s\nprofileArn=%s\nroleArn=%s\nregion={{pinnedRegion}}\n%s\n' "{{BeginMarker}}" "$TA_ARN" "$PROFILE_ARN" "$ROLE_ARN" "{{EndMarker}}"
              echo "Paste the block above back into Connapse."
            else
              echo "Setup did not complete: $FAILED"
            fi
            """;

        return script.Replace("\r\n", "\n");
    }

    private static string SanitiseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return string.Empty;
        string trimmed = region.Trim();
        if (trimmed.Length is 0 or > 32) return string.Empty;
        foreach (char c in trimmed)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) return string.Empty;
        }
        return trimmed;
    }
}
