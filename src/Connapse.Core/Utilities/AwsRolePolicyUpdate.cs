using System.Text.Json;

namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the command that brings Connapse's role permissions up to date.
/// </summary>
/// <remarks>
/// <see cref="S3SetupPolicy.ForManagedIdentity"/> is applied to the role once, at setup. Widening it
/// later — a new action for a new feature — does nothing for a role that already exists, which then
/// fails the new action with <c>AccessDenied</c>. This regenerates the same <c>ConnapseRead</c>
/// policy so an administrator can re-apply it.
/// <para>
/// Connapse does not run it. A runtime identity that could rewrite its own policy could grant itself
/// anything, so IAM editing stays with the administrator's own credentials — the same rule as the
/// initial setup and the grant script. <c>iam:PutRolePolicy</c> replaces an inline policy of the
/// same name in place, so re-applying updates it and touches nothing else.
/// </para>
/// </remarks>
public static class AwsRolePolicyUpdate
{
    /// <summary>The inline policy name, matching what <c>AwsRolesAnywhereSetup</c> attaches.</summary>
    public const string PolicyName = "ConnapseRead";

    /// <summary>
    /// The <c>put-role-policy</c> command for the role at <paramref name="roleArn"/>, or null when
    /// the ARN will not parse (in which case the caller shows the ambient path instead).
    /// </summary>
    public static string? GenerateCommand(string? roleArn)
    {
        if (ParseRoleArn(roleArn) is not var (account, roleName))
            return null;

        // Single-line JSON on purpose. A multi-line policy inside single quotes works in CloudShell
        // (bash) but breaks in PowerShell, where a single-quoted string cannot span lines — so the
        // command a Windows admin pastes locally would fail. Compacted, it is one safe argument.
        string policy = Compact(PolicyDocument(account));

        return $"aws iam put-role-policy --role-name {roleName} "
             + $"--policy-name {PolicyName} --policy-document '{policy}'";
    }

    /// <summary>Re-serialises a policy document onto one line.</summary>
    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    /// <summary>
    /// The <c>ConnapseRead</c> policy document, for a role Connapse did not create (ambient / BYO).
    /// </summary>
    /// <param name="account">
    /// The AWS account, substituted into the policy. When null the placeholder is kept, so the admin
    /// substitutes their own.
    /// </param>
    public static string PolicyDocument(string? account) =>
        S3SetupPolicy.ForManagedIdentity().Replace(
            S3SetupPolicy.AccountPlaceholder,
            string.IsNullOrWhiteSpace(account) ? S3SetupPolicy.AccountPlaceholder : account);

    /// <summary>Pulls the account and role <b>name</b> out of a role ARN.</summary>
    /// <remarks>
    /// <c>put-role-policy</c> takes the role name, not its path, so a path-prefixed ARN
    /// (<c>role/team/name</c>) reduces to its last segment.
    /// </remarks>
    private static (string Account, string RoleName)? ParseRoleArn(string? roleArn)
    {
        if (string.IsNullOrWhiteSpace(roleArn))
            return null;

        // arn:aws:iam::<account>:role/<optional/path/><name>
        string[] parts = roleArn.Split(':');
        if (parts.Length < 6 || parts[2] != "iam")
            return null;

        string account = parts[4];
        if (account.Length == 0 || !account.All(char.IsAsciiDigit))
            return null;

        const string prefix = "role/";
        if (!parts[5].StartsWith(prefix, StringComparison.Ordinal))
            return null;

        string path = parts[5][prefix.Length..];
        string roleName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        // Enforce IAM's role-name grammar before this name is interpolated, unquoted, into a shell
        // command an administrator is told to run. AWS allows [\w+=,.@-] up to 64 chars; anything
        // else (a space, a quote, a semicolon) cannot be a real role name and must not reach a shell.
        // The normal save path validates the ARN against AWS, but a migrated or tampered stored value
        // must not be trusted to have done so.
        if (roleName.Length is 0 or > 64)
            return null;

        if (!roleName.All(c =>
                char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '=' or ',' or '.' or '@' or '-'))
            return null;

        return (account, roleName);
    }
}
