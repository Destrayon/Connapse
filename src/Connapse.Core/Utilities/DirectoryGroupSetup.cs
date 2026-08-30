namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the command that finds an administrator's Identity Center groups, and optionally makes
/// one, so grants can be held by a group rather than by each person.
/// </summary>
/// <remarks>
/// Group-held grants are the shape this feature is meant to be used in. A grant names a grantee,
/// and when that grantee is a group, adding and removing people becomes a directory operation —
/// which is where joiner/mover/leaver already happens — instead of an edit to S3. A grant held by
/// a person is removed by nothing anybody's offboarding process does.
/// <para>
/// Discovery first, creation only if asked. Most organisations synchronise groups into Identity
/// Center from Okta or Entra over SCIM, and those groups are the right grantees; a script that
/// created one regardless would add a group their identity provider does not know about.
/// </para>
/// <para>
/// <b>Creating one is nevertheless supported and is sometimes the only option.</b> AWS keeps the
/// Identity Store mutation APIs open under SCIM and names the cases: Google Workspace and PingOne
/// cannot provision groups over SCIM at all, and pre-provisioning ahead of a sync is legitimate.
/// What AWS warns about is drift — SCIM reconciles deltas, so a group made here is not corrected
/// later — and privilege escalation from adding people to groups that grant access. Hence: the
/// administrator names the group deliberately, and the only membership this script writes is the
/// one directory user who already connected.
/// </para>
/// </remarks>
public static class DirectoryGroupSetup
{
    /// <summary>Names Identity Center refuses, whatever else it accepts.</summary>
    private static readonly string[] ReservedNames = ["Administrator", "AWSAdministrators"];

    /// <summary>The Identity Store actions the script calls, for the operator to check first.</summary>
    /// <remarks>
    /// The last three are only reached when a group name was given. An organisation that blocks
    /// mutation with a service control policy — which AWS suggests as a way to keep the identity
    /// provider authoritative — refuses them, and the script says so rather than looking broken.
    /// </remarks>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "identitystore:ListGroupMembershipsForMember",
        "identitystore:DescribeGroup",
        "identitystore:GetGroupId",
        "identitystore:CreateGroup",
        "identitystore:CreateGroupMembership"
    ];

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <param name="region">Where the Identity Center instance lives.</param>
    /// <param name="identityStoreId">The directory to look in, <c>d-…</c>.</param>
    /// <param name="directoryUserId">
    /// The connected person's identity store id, which is whose groups are listed and who is added
    /// to a newly created one. Empty when nobody has connected yet, and the script then only
    /// creates.
    /// </param>
    /// <param name="groupName">
    /// A group to create, or null to only discover. Created only if no group of that name exists,
    /// so running this twice does not make two.
    /// </param>
    /// <remarks>
    /// Every command is tested with <c>if ! VAR=$(...)</c> rather than by reading <c>$?</c>
    /// afterwards. The two behave the same until a line is inserted between them, at which point
    /// the check silently starts reporting on the wrong command.
    /// </remarks>
    public static string GenerateScript(
        string? region, string? identityStoreId, string? directoryUserId, string? groupName = null)
    {
        string pinnedRegion = AccessGrantsSetup.SanitiseRegion(region);
        string store = SanitiseId(identityStoreId);
        string user = SanitiseId(directoryUserId);
        string name = SanitiseGroupName(groupName);

        return $$"""
        # Finds the Identity Center groups a connected person belongs to, so an access grant can be
        # held by a group rather than by each individual. Creates a group only if you named one in
        # Connapse, and adds only that one person to it.
        #
        # It creates NO access grant. Who may read what stays yours to decide; the command to do it
        # is printed at the end for you to run when you are ready.

        FAILED=""
        REGION="{{pinnedRegion}}"
        STORE="{{store}}"
        USER_ID="{{user}}"
        GROUP_NAME="{{name}}"
        GROUP_ID=""

        [ -n "$REGION" ] || { echo 'No region. Locate your Identity Center instance first.'; FAILED=1; }
        [ -n "$STORE" ] || { echo 'No identity store. Locate your Identity Center instance first.'; FAILED=1; }

        # What the person already belongs to. For most directories these are synchronised from your
        # identity provider and are the groups you want to grant to.
        if [ -z "$FAILED" ] && [ -n "$USER_ID" ]; then
          echo 'Groups this person already belongs to:'
          if ! MEMBERSHIPS=$(aws identitystore list-group-memberships-for-member --region "$REGION" --identity-store-id "$STORE" --member-id UserId="$USER_ID" --query 'GroupMemberships[].GroupId' --output text 2>&1); then
            echo "  Could not read them: $MEMBERSHIPS"
            FAILED=1
          elif [ -z "$MEMBERSHIPS" ] || [ "$MEMBERSHIPS" = 'None' ]; then
            echo '  (none)'
          else
            for G in $MEMBERSHIPS; do
              DISPLAY=$(aws identitystore describe-group --region "$REGION" --identity-store-id "$STORE" --group-id "$G" --query DisplayName --output text 2>/dev/null || echo '?')
              echo "  $DISPLAY  $G"
            done
          fi
          echo
        fi

        # Only when you named one. Looked up first, so running this again finds the group rather
        # than making a second one with the same name.
        if [ -z "$FAILED" ] && [ -n "$GROUP_NAME" ]; then
          GROUP_ID=$(aws identitystore get-group-id --region "$REGION" --identity-store-id "$STORE" --alternate-identifier "UniqueAttribute={AttributePath=displayName,AttributeValue=$GROUP_NAME}" --query GroupId --output text 2>/dev/null || true)

          if [ -n "$GROUP_ID" ] && [ "$GROUP_ID" != 'None' ]; then
            echo "Group $GROUP_NAME already exists: $GROUP_ID"
          elif CREATED=$(aws identitystore create-group --region "$REGION" --identity-store-id "$STORE" --display-name "$GROUP_NAME" --query GroupId --output text 2>&1); then
            GROUP_ID="$CREATED"
            echo "Created $GROUP_NAME: $GROUP_ID"
            echo 'Note: a group created here is not known to your identity provider, and a later'
            echo 'sync will not remove or reconcile it.'
          else
            echo "Could not create $GROUP_NAME: $CREATED"
            case "$CREATED" in
              *AccessDenied*)
                echo 'Your organisation may block creating groups here so that your identity'
                echo 'provider stays authoritative. Create the group there instead, let it'
                echo 'synchronise, then run this again to see it listed above.' ;;
            esac
            FAILED=1
          fi

          # Only the one person who connected. Adding anybody else to a group that carries a grant
          # is the privilege escalation AWS warns about, and is not a setup script's decision.
          if [ -z "$FAILED" ] && [ -n "$USER_ID" ]; then
            if ADDED=$(aws identitystore create-group-membership --region "$REGION" --identity-store-id "$STORE" --group-id "$GROUP_ID" --member-id UserId="$USER_ID" 2>&1); then
              echo 'Added the connected user to it.'
            else
              case "$ADDED" in
                *Conflict*) echo 'The connected user was already a member.' ;;
                *) echo "Could not add the connected user: $ADDED"; FAILED=1 ;;
              esac
            fi
          fi
        fi

        if [ -n "$FAILED" ]; then
          echo
          echo 'Something above failed. Nothing was recorded in Connapse; fix it and run this again.'
        else
          echo
          echo 'To let a group read a bucket, run this with a group id from above, replacing'
          echo 'YOUR-BUCKET and GROUP-ID. Connapse honours it within a minute.'
          # Single quotes on purpose: the command substitution is printed for the operator to
          # copy, not expanded here. Expanding it would bake this session's account into a line
          # they may paste somewhere else entirely. A directive takes no trailing prose, so the
          # reason lives here and the directive stands alone.
          # shellcheck disable=SC2016
          echo '  aws s3control create-access-grant --account-id "$(aws sts get-caller-identity --query Account --output text)" --access-grants-location-id default --access-grants-location-configuration S3SubPrefix=YOUR-BUCKET/* --permission READ --grantee GranteeType=DIRECTORY_GROUP,GranteeIdentifier=GROUP-ID'
        fi
        """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Coerces a group name to something Identity Center accepts and a shell cannot misread.
    /// </summary>
    /// <remarks>
    /// The name reaches a double-quoted assignment, so a quote, a backslash or a <c>$</c> would end
    /// the string early or expand into something else. Rejected rather than escaped: the result is
    /// a display name someone reads later, and a mangled one is worse than being asked again.
    /// Identity Center allows up to 1024 characters; this stops well short, because the name also
    /// has to be legible in the listing above.
    /// </remarks>
    public static string SanitiseGroupName(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > 128)
            return string.Empty;

        if (ReservedNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return string.Empty;

        bool safe = trimmed.All(c =>
            !char.IsControl(c) && c is not '"' and not '\'' and not '\\' and not '$' and not '`');

        return safe ? trimmed : string.Empty;
    }

    /// <summary>
    /// Keeps an identity store or user id only if it looks like one.
    /// </summary>
    /// <remarks>
    /// Both are machine-generated — <c>d-</c> followed by hex, or a UUID optionally prefixed with
    /// the store id — so an allowlist of the characters those forms use rejects anything that could
    /// carry shell syntax without ever refusing a real value.
    /// </remarks>
    public static string SanitiseId(string? id)
    {
        string trimmed = id?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > 64)
            return string.Empty;

        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-')
            ? trimmed
            : string.Empty;
    }
}
