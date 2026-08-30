namespace Connapse.Core.Utilities;

/// <summary>The access key a generated IAM user was created with.</summary>
/// <param name="UserName">The IAM user the key belongs to.</param>
/// <param name="AccessKeyId">Public half. Safe to display.</param>
/// <param name="SecretAccessKey">
/// Private half. AWS returns this exactly once, at creation, and will never show it again.
/// </param>
public record AwsGeneratedKey(string UserName, string AccessKeyId, string SecretAccessKey);

/// <summary>
/// Builds the command that gives Connapse an AWS identity of its own, and reads back the key it
/// prints.
/// </summary>
/// <remarks>
/// An identity for the application, not a borrowed one. Pointing Connapse at an administrator's
/// credentials makes its reach depend on whose credentials those were — and change when that
/// person's permissions change, or vanish when they leave. A user named for Connapse has the same
/// access regardless of who ran the script.
/// <para>
/// The user is created <i>with</i> its policy attached, in one script, so the key is never briefly
/// broader than intended. Current guidance is to prefer short-lived credentials and, where a static
/// key is unavoidable, to scope it narrowly and rotate it: scoping happens here, rotation is
/// offered in the UI, and an instance role — which needs none of this — is detected first so that
/// deployments on AWS never reach this flow.
/// </para>
/// </remarks>
public static class AwsIamUserSetup
{
    public const string BeginMarker = "----- BEGIN CONNAPSE AWS KEY -----";

    public const string EndMarker = "----- END CONNAPSE AWS KEY -----";

    /// <summary>Default name for the user, chosen so it is obvious what created it.</summary>
    public const string DefaultUserName = "connapse-reader";

    /// <summary>The IAM actions the script itself needs.</summary>
    /// <remarks>
    /// <c>iam:GetUser</c> is in the list because the script calls it first. Without it the check
    /// returns AccessDenied, which is indistinguishable from "no such user" at the shell, so the
    /// script decides an existing user is absent and falls through to <c>create-user</c> — which
    /// then fails for a different reason and reads as a broken script.
    /// </remarks>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "iam:GetUser", "iam:CreateUser", "iam:PutUserPolicy", "iam:CreateAccessKey"
    ];

    /// <summary>
    /// The command to paste into AWS CloudShell.
    /// </summary>
    /// <param name="userName">IAM user name; defaults to <see cref="DefaultUserName"/>.</param>
    /// <remarks>
    /// Unlike the other setup scripts in this project, this one writes. It is shown in full rather
    /// than downloaded or piped from a URL, so the administrator can read exactly what it creates
    /// before running it — an IAM user, one inline policy, one access key, and nothing else.
    /// <para>
    /// An inline policy rather than a managed one: it is attached to this user alone and is deleted
    /// with it, so removing the user leaves nothing behind to find later.
    /// </para>
    /// <para>
    /// No parameter for the scope. This is the easy path, and it grants what Connapse needs to do
    /// its job — <see cref="S3SetupPolicy.ForManagedIdentity"/>, read-only. An operator who wants a
    /// different shape of credential makes one themselves and points Connapse at it, which is a
    /// supported arrangement rather than a fallback.
    /// </para>
    /// </remarks>
    public static string GenerateScript(string? userName = null)
    {
        string user = SanitiseUserName(userName);
        string policy = S3SetupPolicy.ForManagedIdentity();
        string scopeSummary = S3SetupPolicy.ManagedIdentitySummary;

        // Single-quoted heredoc so the shell expands nothing inside the policy document.
        return $$"""
        # Sets up an AWS identity for Connapse: one IAM user, one read-only policy, and one
        # access key. Run against an identity Connapse already made, it brings the policy up to
        # date and leaves the key alone.
        #
        # The policy allows: {{scopeSummary}}

        # Set by any step that fails. No `set -e` and no bare `exit`: both end the *session* when
        # these lines are pasted into an interactive shell, which is how CloudShell disconnects
        # part-way through rather than reporting a problem.
        FAILED=""
        USER='{{user}}'

        EXISTS=""
        aws iam get-user --user-name "$USER" >/dev/null 2>&1 && EXISTS=1

        # Only an identity Connapse created. One that happens to share the name is somebody else's,
        # and rewriting its policy would be taking over an account this script cannot reason about.
        if [ -n "$EXISTS" ]; then
          OWNER=$(aws iam list-user-tags --user-name "$USER" \
                    --query "Tags[?Key=='CreatedBy'].Value" --output text 2>/dev/null || true)
          if [ "$OWNER" != 'Connapse' ]; then
            echo "An IAM user named $USER already exists, and Connapse did not create it."
            echo 'Use a different name in Connapse, or remove that user yourself, then run this again.'
            FAILED=1
          fi
        fi

        if [ -z "$FAILED" ] && [ -z "$EXISTS" ]; then
          aws iam create-user --user-name "$USER" --tags Key=CreatedBy,Value=Connapse >/dev/null || FAILED=1
        fi

        # Written either way. On a new identity this is the grant; on one that already exists it
        # replaces an older grant with the current one, which is how permissions added in a later
        # version reach an installation that already ran this. put-user-policy replaces the document
        # in full and does not touch access keys.
        #
        # Inline rather than managed, so the grant is deleted along with the user rather than
        # lingering as an orphan.
        if [ -z "$FAILED" ]; then
          aws iam put-user-policy --user-name "$USER" \
            --policy-name ConnapseRead \
            --policy-document '{{policy}}' || FAILED=1
        fi

        # Only for an identity that did not exist a moment ago. Creating a second key for one that
        # did would leave the running deployment authenticating with the first, and this block
        # telling you to paste the second.
        if [ -z "$FAILED" ] && [ -z "$EXISTS" ]; then
          # The secret is returned once and never again, which is why the block below is the only
          # chance to copy it.
          KEY=$(aws iam create-access-key --user-name "$USER" \
                  --query 'AccessKey.[AccessKeyId,SecretAccessKey]' --output text) || FAILED=1

          BLOCK=$(
            printf '%s\n' '{{BeginMarker}}'
            printf 'user=%s\n' "$USER"
            # '%s\n', not '%s'. Command substitution strips the trailing newline, and `read`
            # returns non-zero at EOF without running the loop body — so the one line of output was
            # silently dropped and the block came back with no key in it at all.
            printf '%s\n' "$KEY" | while IFS=$(printf '\t') read -r ID SECRET; do
              [ -z "$ID" ] && continue
              printf 'accessKeyId=%s\n' "$ID"
              printf 'secretAccessKey=%s\n' "$SECRET"
            done
            printf '%s\n' '{{EndMarker}}'
          )

          printf '\n%s\n\n' "$BLOCK"
          echo 'Copy the block above into Connapse. The secret is not recoverable afterwards.'
        fi

        if [ -n "$FAILED" ]; then
          echo
          echo 'Something above failed. Nothing was recorded in Connapse; fix it and run this again.'
        elif [ -n "$EXISTS" ]; then
          echo
          echo "Permissions for $USER are up to date."
          echo 'Its access key is unchanged, so there is nothing to paste back into Connapse.'
        fi
        """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Reads the key block the script printed. Returns null when the text has no usable one.
    /// </summary>
    /// <remarks>
    /// Anchored on the <b>last</b> marker pair. The script contains both markers in its own text —
    /// printing them is its job — so a pasted terminal buffer holds each twice, and taking the
    /// first pair selects the echoed source rather than the output.
    /// </remarks>
    public static AwsGeneratedKey? ParseResult(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        int start = end < 0 ? -1 : pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return null;

        string? user = null, id = null, secret = null;

        foreach (string raw in pasted[(start + BeginMarker.Length)..end]
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string value = line[(split + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (line[..split].Trim())
            {
                case "user": user = value; break;
                case "accessKeyId": id = value; break;
                case "secretAccessKey": secret = value; break;
            }
        }

        // A block without both halves of the key is not a partial success — a key id with no secret
        // authenticates nothing, and storing it would produce a connection that fails at sync time.
        return id is null || secret is null
            ? null
            : new AwsGeneratedKey(user ?? DefaultUserName, id, secret);
    }

    /// <summary>
    /// Coerces a user name to what IAM accepts, so a bad one fails here and not in the shell.
    /// </summary>
    /// <remarks>
    /// IAM allows <c>[\w+=,.@-]</c> up to 64 characters. A space would break the quoting in the
    /// generated command line before AWS ever saw it.
    /// </remarks>
    public static string SanitiseUserName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return DefaultUserName;

        string cleaned = new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '=' or ',' or '.' or '@' or '-'
                ? c
                : '-')
            .ToArray()).Trim('-');

        return cleaned.Length == 0 ? DefaultUserName : cleaned[..Math.Min(cleaned.Length, 64)];
    }
}
