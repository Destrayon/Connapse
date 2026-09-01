namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the command that lists Identity Center groups, and optionally makes one, so grants can
/// be held by a group rather than by each person.
/// </summary>
/// <remarks>
/// It prints no grant command of its own. One would have to name a bucket, and this step
/// cannot know which buckets exist — that is a property of a connection, and the version that
/// guessed shipped a placeholder that was duly run unreplaced. <c>AccessGrantScript</c> builds
/// the real one, on the connection, from the group id this records.
/// <para>
/// Group-held grants are the shape this feature is meant to be used in. A grant names a grantee,
/// and when that grantee is a group, adding and removing people becomes a directory operation —
/// which is where joiner/mover/leaver already happens — instead of an edit to S3. A grant held by
/// a person is removed by nothing anybody's offboarding process does.
/// </para>
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
    /// <summary>
    /// Delimits the block an administrator pastes back, so Connapse can hold the group id.
    /// </summary>
    /// <remarks>
    /// The same shape the other two setup steps use. Without it the id is on screen for a
    /// moment and then gone, which is why the grant command it feeds had a placeholder where
    /// the grantee belonged.
    /// </remarks>
    public const string BeginMarker = "----- BEGIN CONNAPSE GROUP -----";

    public const string EndMarker = "----- END CONNAPSE GROUP -----";

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
        "identitystore:ListGroups",
        "identitystore:DescribeGroup",
        "identitystore:GetGroupId",
        "identitystore:CreateGroup",
        "identitystore:CreateGroupMembership"
    ];

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <param name="region">Where the Identity Center instance lives.</param>
    /// <param name="identityStoreId">The directory to look in, <c>d-…</c>.</param>
    /// <param name="directoryUserId">
    /// The connected person's identity store id, when available. It is added to a group created or
    /// selected by name, but group discovery does not depend on it.
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
        # Lists Identity Center groups. If GROUP_NAME is set, uses that group or creates it.

        FAILED=""
        REGION="{{pinnedRegion}}"
        STORE="{{store}}"
        USER_ID="{{user}}"
        GROUP_NAME="{{name}}"
        GROUP_ID=""
        RECORD_NAME=""

        [ -n "$REGION" ] || { echo 'No region. Locate your Identity Center instance first.'; FAILED=1; }
        [ -n "$STORE" ] || { echo 'No identity store. Locate your Identity Center instance first.'; FAILED=1; }

        # List every available group. This works before anyone has connected to Connapse.
        if [ -z "$FAILED" ] && [ -z "$GROUP_NAME" ]; then
          echo 'Available groups:'
          if ! GROUP_IDS=$(aws identitystore list-groups --region "$REGION" --identity-store-id "$STORE" --query 'Groups[].GroupId' --output text 2>&1); then
            echo "  Could not read them: $GROUP_IDS"
            FAILED=1
          elif [ -z "$GROUP_IDS" ] || [ "$GROUP_IDS" = 'None' ]; then
            echo '  (none)'
            echo 'No groups found. Enter a group name in Connapse to create one, or use the manual fields.'
          else
            BLOCK=$(
              printf '%s\n' '{{BeginMarker}}'
              for G in $GROUP_IDS; do
                DISPLAY=$(aws identitystore describe-group --region "$REGION" --identity-store-id "$STORE" --group-id "$G" --query DisplayName --output text 2>/dev/null || echo '?')
                echo "  $DISPLAY  $G" >&2
                printf 'groupId=%s\n' "$G"
                printf 'groupName=%s\n' "$DISPLAY"
              done
              printf '%s\n' '{{EndMarker}}'
            )
            printf '\n%s\n\n' "$BLOCK"
            echo 'Copy the block above into Connapse and choose the group to use.'
          fi
        fi

        # Use an existing group by exact name, or create it when it does not exist.
        if [ -z "$FAILED" ] && [ -n "$GROUP_NAME" ]; then
          GROUP_ID=$(aws identitystore get-group-id --region "$REGION" --identity-store-id "$STORE" --alternate-identifier "UniqueAttribute={AttributePath=displayName,AttributeValue=$GROUP_NAME}" --query GroupId --output text 2>/dev/null || true)

          if [ -n "$GROUP_ID" ] && [ "$GROUP_ID" != 'None' ]; then
            RECORD_NAME="$GROUP_NAME"
            echo "Group $GROUP_NAME already exists: $GROUP_ID"
          elif CREATED=$(aws identitystore create-group --region "$REGION" --identity-store-id "$STORE" --display-name "$GROUP_NAME" --query GroupId --output text 2>&1); then
            GROUP_ID="$CREATED"
            RECORD_NAME="$GROUP_NAME"
            echo "Created $GROUP_NAME: $GROUP_ID"
            echo 'This group is local to Identity Center; your identity provider will not manage it.'
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

          # Add only the connected administrator when one is available.
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

        # Return the selected or created group.
        if [ -z "$FAILED" ] && [ -n "$GROUP_ID" ] && [ "$GROUP_ID" != 'None' ]; then
          BLOCK=$(
            printf '%s\n' '{{BeginMarker}}'
            printf 'groupId=%s\n' "$GROUP_ID"
            printf 'groupName=%s\n' "$RECORD_NAME"
            printf '%s\n' '{{EndMarker}}'
          )
          printf '\n%s\n\n' "$BLOCK"
          echo 'Copy the block above into Connapse so it can name this group for you.'
        fi

        if [ -n "$FAILED" ]; then
          echo
          echo 'Something above failed. Nothing was recorded in Connapse; fix it and run this again.'
        elif [ -n "$GROUP_ID" ] && [ "$GROUP_ID" != 'None' ]; then
          echo
          echo 'Paste the block above into Connapse.'
        fi
        """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Reads the block the script printed. Returns null when the text has no usable one.
    /// </summary>
    /// <remarks>
    /// Anchored on the <b>last</b> marker pair. The script contains both markers in its own text,
    /// so a pasted terminal buffer holds each twice: once in the echoed source, once in the real
    /// output below it. Taking the first pair selects the echo, whose body is printf lines that
    /// parse to no fields at all.
    /// </remarks>
    public static (string Id, string Name)? ParseResult(string? pasted)
    {
        var groups = ParseResults(pasted);
        return groups.Count == 0 ? null : groups[^1];
    }

    /// <summary>Reads every group from the last result block.</summary>
    public static IReadOnlyList<(string Id, string Name)> ParseResults(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return [];

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        int start = end < 0 ? -1 : pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return [];

        var groups = new List<(string Id, string Name)>();
        string? id = null, name = null;

        void AddCurrent()
        {
            string cleanId = SanitiseId(id);
            if (cleanId.Length > 0)
                groups.Add((cleanId, SanitiseDisplayName(name)));

            id = null;
            name = null;
        }

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
                case "groupId":
                    AddCurrent();
                    id = value;
                    break;
                case "groupName": name = value; break;
            }
        }

        AddCurrent();
        return groups;
    }

    /// <summary>Removes terminal control characters without rejecting valid display punctuation.</summary>
    private static string SanitiseDisplayName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        string clean = new(name.Where(c => !char.IsControl(c)).ToArray());
        return clean.Trim();
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
