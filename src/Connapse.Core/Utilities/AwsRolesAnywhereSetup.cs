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
}
