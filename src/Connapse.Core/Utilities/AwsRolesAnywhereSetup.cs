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

        if (!TryParseArn(trustAnchor, "rolesanywhere", "trust-anchor", out string taRegion, out string taAccount))
            return null;
        if (!TryParseArn(profile, "rolesanywhere", "profile", out string profileRegion, out string profileAccount))
            return null;
        // IAM is global: the role ARN has an empty region segment.
        if (!TryParseArn(role, "iam", "role", out string roleRegion, out string roleAccount))
            return null;
        if (roleRegion.Length != 0)
            return null;

        // The region field itself must be a clean AWS region (it builds the runtime endpoint host).
        if (region != SanitiseRegion(region))
            return null;
        // The trust anchor and profile must live in that same region, and all three in one account.
        if (taRegion != region || profileRegion != region)
            return null;
        if (taAccount != profileAccount || taAccount != roleAccount)
            return null;

        return new AwsRolesAnywhereArns(trustAnchor, profile, role, region);
    }

    // arn:partition:service:region:account:resourcetype/resource-id
    private static bool TryParseArn(string arn, string expectedService, string expectedResourceType,
        out string region, out string account)
    {
        region = string.Empty;
        account = string.Empty;
        string[] parts = arn.Split(':', 6);
        if (parts.Length < 6) return false;
        if (parts[0] != "arn") return false;
        if (parts[1] is not ("aws" or "aws-us-gov" or "aws-cn")) return false;
        if (parts[2] != expectedService) return false;
        region = parts[3];
        account = parts[4];
        if (account.Length > 0 && !account.All(char.IsAsciiDigit)) return false;
        return parts[5].StartsWith(expectedResourceType + "/", StringComparison.Ordinal)
            && parts[5].Length > expectedResourceType.Length + 1;
    }

    /// <summary>
    /// The CloudShell script that provisions Roles Anywhere access for this instance. Every resource —
    /// trust anchor, role, and profile — is per-instance, named from the CA certificate's fingerprint, so
    /// concurrent runs from different instances never collide or race. The role's trust policy pins
    /// (ArnEquals) to this instance's own trust anchor rather than any trust anchor in the account.
    /// </summary>
    public static string GenerateScript(string caCertificatePem, string? region)
    {
        string cert = caCertificatePem.Replace("\r\n", "\n").Trim();
        string pinnedRegion = SanitiseRegion(region);
        string policy = S3SetupPolicy.ForManagedIdentity().Replace("\r\n", "\n");
        string account = S3SetupPolicy.AccountPlaceholder;

        string script = $$"""
            # Provisions THIS Connapse instance's own IAM Roles Anywhere access. Every resource is
            # per-instance, named by the certificate fingerprint, so runs never collide or race.
            FAILED=""
            REGION="{{pinnedRegion}}"
            if [ -z "$REGION" ]; then FAILED="no valid AWS region was provided"; fi

            ACCOUNT=""
            if [ -z "$FAILED" ]; then
              ACCOUNT=$(aws sts get-caller-identity --query Account --output text) || FAILED="could not resolve the AWS account"
            fi

            CA_CERT='{{cert}}'
            NAME=""
            if [ -z "$FAILED" ]; then
              FP=$(printf '%s' "$CA_CERT" | openssl x509 -noout -fingerprint -sha256 | sed 's/.*=//; s/://g' | cut -c1-16 | tr 'A-Z' 'a-z')
              if [ -z "$FP" ]; then FAILED="could not read the certificate fingerprint"; fi
              NAME="{{NamePrefix}}-ra-$FP"
            fi

            # --- Trust anchor from the CA cert, created FIRST so the role can pin to it ---
            TA_ARN=""
            if [ -z "$FAILED" ]; then
              TA_ARN=$(aws rolesanywhere list-trust-anchors --region "$REGION" --query "trustAnchors[?name=='$NAME'].trustAnchorArn | [0]" --output text)
              if [ "$TA_ARN" = "None" ] || [ -z "$TA_ARN" ]; then
                TA_SRC=$(mktemp)
                jq -n --arg cert "$CA_CERT" '{sourceData:{x509CertificateData:$cert},sourceType:"CERTIFICATE_BUNDLE"}' > "$TA_SRC" || FAILED="could not build the trust-anchor source"
                if [ -z "$FAILED" ]; then
                  TA_ARN=$(aws rolesanywhere create-trust-anchor --region "$REGION" --name "$NAME" --source "file://$TA_SRC" --enabled --query 'trustAnchor.trustAnchorArn' --output text) || FAILED="could not create the trust anchor"
                fi
                rm -f "$TA_SRC"
              fi
            fi

            # --- Per-instance role, trust pinned (ArnEquals) to THIS trust anchor ---
            ROLE_ARN=""
            if [ -z "$FAILED" ]; then
              if aws iam get-role --role-name "$NAME" >/dev/null 2>&1; then
                OWNER=$(aws iam list-role-tags --role-name "$NAME" --query "Tags[?Key=='CreatedBy'].Value" --output text)
                if [ "$OWNER" != "Connapse" ]; then FAILED="a role named $NAME already exists and Connapse did not create it"; fi
                ROLE_ARN=$(aws iam get-role --role-name "$NAME" --query 'Role.Arn' --output text)
              else
                TRUST='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"rolesanywhere.amazonaws.com"},"Action":["sts:AssumeRole","sts:TagSession","sts:SetSourceIdentity"],"Condition":{"ArnEquals":{"aws:SourceArn":"__TA_ARN__"} } }]}'
                TRUST=${TRUST//__TA_ARN__/$TA_ARN}
                ROLE_ARN=$(aws iam create-role --role-name "$NAME" --assume-role-policy-document "$TRUST" --tags Key=CreatedBy,Value=Connapse --query 'Role.Arn' --output text) || FAILED="could not create the role"
              fi
            fi

            # --- ConnapseRead policy ---
            if [ -z "$FAILED" ]; then
              POLICY='{{policy}}'
              POLICY=${POLICY//{{account}}/$ACCOUNT}
              aws iam put-role-policy --role-name "$NAME" --policy-name ConnapseRead --policy-document "$POLICY" || FAILED="could not apply the ConnapseRead policy"
            fi

            # --- Per-instance profile ---
            PROFILE_ARN=""
            if [ -z "$FAILED" ]; then
              PROFILE_ARN=$(aws rolesanywhere list-profiles --region "$REGION" --query "profiles[?name=='$NAME'].profileArn | [0]" --output text)
              if [ "$PROFILE_ARN" = "None" ] || [ -z "$PROFILE_ARN" ]; then
                PROFILE_ARN=$(aws rolesanywhere create-profile --region "$REGION" --name "$NAME" --role-arns "$ROLE_ARN" --enabled --query 'profile.profileArn' --output text) || FAILED="could not create the profile"
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
