namespace Connapse.Core.Utilities;

/// <summary>One IAM Identity Center instance the discovery script found.</summary>
/// <param name="Region">
/// The region whose <c>sso-admin list-instances</c> returned it. The field people most often get
/// wrong, and the one that cannot be derived from anything else the administrator already has.
/// </param>
/// <param name="InstanceArn"><c>arn:aws:sso:::instance/ssoins-…</c>.</param>
/// <param name="IdentityStoreId"><c>d-…</c>, the directory the trusted token issuer resolves into.</param>
public record IdentityCenterInstance(string Region, string InstanceArn, string IdentityStoreId);

/// <summary>
/// Where the account sits relative to AWS Organizations, which decides who can enable an instance
/// and therefore what to tell someone whose scan found none.
/// </summary>
public enum AwsAccountPosture
{
    /// <summary>The script could not tell — usually no permission to call DescribeOrganization.</summary>
    Unknown = 0,

    /// <summary>Not in an organisation, so this account can enable an instance itself.</summary>
    Standalone,

    /// <summary>In an organisation, but not the management account.</summary>
    Member,

    /// <summary>The organisation's management account.</summary>
    Management,
}

/// <summary>What the discovery script reported.</summary>
/// <param name="Instances">
/// Every instance the scan saw. Usually one — Identity Center is one region per organisation — and
/// empty when there is none to see, which is a real answer rather than a failure to parse.
/// </param>
/// <param name="MissingPermissions">
/// Populated when the scan was refused rather than simply finding nothing. The two look identical
/// in the result otherwise, and they need opposite advice.
/// </param>
/// <param name="Posture">
/// Only meaningful when <paramref name="Instances"/> is empty, which is the one case where what to
/// do next depends on it.
/// </param>
public record IdentityCenterSetupResult(
    IReadOnlyList<IdentityCenterInstance> Instances,
    IReadOnlyList<string>? MissingPermissions = null,
    AwsAccountPosture Posture = AwsAccountPosture.Unknown)
{
    public IReadOnlyList<string> MissingPermissions { get; init; } = MissingPermissions ?? [];

    /// <summary>
    /// Whether this account could enable an instance itself rather than needing the organisation's
    /// management account to do it.
    /// </summary>
    /// <remarks>
    /// Enabling an instance is an organisation-wide act, so a member account cannot do it however
    /// much IAM permission it holds. A standalone account has no organisation in the way.
    /// <para>
    /// Deliberately not a judgement about organisation-versus-account instances. The device flow
    /// this replaced needed permission sets, which only an organisation instance has; trusted
    /// identity propagation needs a directory and an application, which either kind provides.
    /// </para>
    /// </remarks>
    public bool CanEnableItself => Posture is AwsAccountPosture.Management or AwsAccountPosture.Standalone;
}

/// <summary>
/// Builds the one command an administrator runs in AWS CloudShell to locate their IAM Identity
/// Center instance, and reads back what it reports.
/// </summary>
/// <remarks>
/// CloudShell rather than a credential field or a CloudFormation callback. It already holds
/// temporary credentials from the administrator's console session, so nothing is created, pasted
/// into Connapse, or stored — the copy-paste round trip <i>is</i> the credential boundary.
/// <para>
/// This runs <b>before</b> <see cref="AccessGrantsSetup"/> and answers the question that one assumes:
/// which region the instance is in. That script looks only in CloudShell's own region, so an
/// instance one region over reads there as an instance that does not exist.
/// </para>
/// </remarks>
public static class IdentityCenterSetup
{
    /// <summary>
    /// Delimits the block the administrator pastes back. Deliberately unmistakable, because the
    /// usual failure is pasting the whole terminal buffer, and that should still work.
    /// </summary>
    public const string BeginMarker = "----- BEGIN CONNAPSE IDENTITY CENTER -----";

    public const string EndMarker = "----- END CONNAPSE IDENTITY CENTER -----";

    /// <summary>
    /// Regions the scan tries when the CloudShell session's own region has no instance.
    /// </summary>
    /// <remarks>
    /// Not every AWS region — that would be a long serial scan for a service that exists in exactly
    /// one place. These are the commercial regions Identity Center is commonly deployed in, ordered
    /// roughly by how often that is true. A miss is not fatal: the script says it scanned and found
    /// nothing, and the administrator can read their region off the console.
    /// <para>
    /// GovCloud and the China partitions are absent on purpose. They need different endpoints and a
    /// different partition in the ARN, so a caller there needs a different script, not a longer list.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> CandidateRegions =
    [
        "us-east-1", "us-west-2", "us-east-2", "us-west-1",
        "eu-west-1", "eu-central-1", "eu-west-2", "eu-north-1", "eu-west-3", "eu-south-1",
        "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ap-northeast-2", "ap-south-1",
        "ca-central-1", "sa-east-1", "ap-east-1", "me-south-1", "af-south-1"
    ];

    /// <summary>
    /// The IAM actions the script calls. Read-only, and stated up front so the administrator can
    /// check them against their own policy before running anything.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "sso:ListInstances",
        "organizations:DescribeOrganization",
        "sts:GetCallerIdentity"
    ];

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <remarks>
    /// Meant to be read before it is run. It is read-only — it lists instances and prints what it
    /// found — but it is shown in full rather than offered as a download or a pipe from a URL, so
    /// the administrator can see exactly what they are agreeing to at the moment they agree to it.
    /// <para>
    /// No heredoc and no quoted string spanning lines. Both put an interactive shell into
    /// continuation mode while the rest of the paste arrives, which is how a long block of this
    /// kind disconnects CloudShell rather than running.
    /// </para>
    /// </remarks>
    public static string GenerateScript()
    {
        string regions = string.Join(" ", CandidateRegions);

        return $$"""
        # Finds your IAM Identity Center instance. Read-only: it lists instances and prints what it
        # found. Nothing is created, changed, or sent anywhere.

        FOUND=""
        DENIED=""

        probe() {
          # --output text with an explicit --query, rather than jq: CloudShell ships jq, but
          # depending on it buys nothing the CLI cannot already do.
          OUT=$(aws sso-admin list-instances --region "$1" --query 'Instances[].[InstanceArn,IdentityStoreId]' --output text 2>&1)
          STATUS=$?

          if [ $STATUS -ne 0 ]; then
            # A denial is worth reporting; anything else is this region simply not being the one,
            # which is the expected outcome for all but one of them.
            case "$OUT" in
              *AccessDenied*|*UnauthorizedOperation*|*not\ authorized*) DENIED="yes" ;;
            esac
            return
          fi

          [ -z "$OUT" ] && return

          # Tab-separated, one instance per line. Held rather than printed so the whole result block
          # comes out together at the end. A herestring rather than a heredoc: same input, one line.
          while IFS=$(printf '\t') read -r ARN STORE; do
            [ -z "$ARN" ] && continue
            FOUND="${FOUND}${1}|${ARN}|${STORE}"$'\n'
          done <<< "$OUT"
        }

        # The session's own region first. For most people this is the answer and the scan below
        # never runs.
        HOME_REGION="${AWS_REGION:-$AWS_DEFAULT_REGION}"
        [ -n "$HOME_REGION" ] && probe "$HOME_REGION"

        # A sso:ListInstances denial is a property of the caller's policy, not of the region, so
        # scanning the other nineteen would be nineteen more calls to be refused the same way.
        if [ -z "$FOUND" ] && [ -z "$DENIED" ]; then
          echo 'Not in this session default region; checking the others. This takes a moment.'
          for R in {{regions}}; do
            [ "$R" = "$HOME_REGION" ] && continue
            probe "$R"
            [ -n "$DENIED" ] && break
            # Identity Center is one region per organisation. The exception is multi-region
            # replication, which the administrator will know they have -- so stopping at the first
            # hit is right for everyone else, and a full scan would be 20 sequential calls for
            # nothing.
            [ -n "$FOUND" ] && break
          done
        fi

        # Where this account sits relative to Organizations. Only consulted when nothing was found,
        # but probed unconditionally so the answer travels in the same block.
        #
        # A denial has to be told apart from "not in an organisation": both leave the query empty,
        # and treating a denial as standalone would offer a create step that AWS rejects.
        ORG_OUT=$(aws organizations describe-organization --query 'Organization.MasterAccountId' --output text 2>&1)
        ORG_STATUS=$?
        CALLER=$(aws sts get-caller-identity --query 'Account' --output text 2>/dev/null)

        if [ $ORG_STATUS -ne 0 ]; then
          case "$ORG_OUT" in
            *AWSOrganizationsNotInUse*) POSTURE="standalone" ;;
            *) POSTURE="unknown" ;;
          esac
        elif [ -z "$CALLER" ]; then
          POSTURE="unknown"
        elif [ "$ORG_OUT" = "$CALLER" ]; then
          POSTURE="management"
        else
          POSTURE="member"
        fi

        # Built whole, then printed once.
        #
        # Pasting a multi-line script into an interactive shell makes it echo every line back.
        # Printing the block piece by piece let that echo land between the markers, so the thing the
        # administrator is asked to check was half script. Capturing it first puts all the echo
        # above the BEGIN marker, where it belongs.
        #
        # The markers go through %s rather than being the format string themselves. They begin with
        # dashes, and printf reads a leading '-' as an option.
        BLOCK=$(
          printf '%s\n' '{{BeginMarker}}'
          printf 'accountType=%s\n' "$POSTURE"

          if [ -n "$FOUND" ]; then
            printf '%s' "$FOUND" | while IFS='|' read -r REGION ARN STORE; do
              [ -z "$REGION" ] && continue
              printf 'region=%s\n' "$REGION"
              printf 'instanceArn=%s\n' "$ARN"
              printf 'identityStoreId=%s\n' "$STORE"
            done
          elif [ -n "$DENIED" ]; then
            printf 'missingPermission=%s\n' 'sso:ListInstances'
          fi

          printf '%s\n' '{{EndMarker}}'
        )

        printf '\n%s\n\n' "$BLOCK"
        echo 'Copy the block above, including both marker lines, back into Connapse.'
        """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Reads the block the script printed. Returns null when the text has no usable one.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose. Administrators paste whole terminal buffers, with prompts, wrapping and
    /// the command echoed above the output — so this finds the markers rather than expecting the
    /// text to begin at them, and ignores anything outside.
    /// <para>
    /// Anchored on the <b>last</b> marker pair. The script contains both literals, since printing
    /// them is its job, so a pasted buffer holds each twice: once in the echoed source, once in the
    /// real output below it. The first pair selects the echo, whose body is nothing but
    /// <c>printf</c> lines that parse to no fields at all — telling an administrator with a
    /// perfectly good instance that there wasn't one.
    /// </para>
    /// <para>
    /// A block containing neither an instance nor a denial still parses, returning an empty result.
    /// That is a real outcome — the scan ran and found nothing — and it needs to reach the caller as
    /// such rather than as "you pasted the wrong thing".
    /// </para>
    /// </remarks>
    public static IdentityCenterSetupResult? ParseResult(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        int start = end < 0 ? -1 : pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);

        // Without both markers there is nothing to be confident about. Guessing at loose key=value
        // lines would happily accept a paste from somewhere else entirely.
        if (start < 0 || end <= start)
            return null;

        string body = pasted[(start + BeginMarker.Length)..end];

        var instances = new List<IdentityCenterInstance>();
        var missing = new List<string>();
        var posture = AwsAccountPosture.Unknown;

        string? region = null, arn = null, store = null;

        // region starts each record, so meeting one again means the previous record is done.
        void Flush()
        {
            if (region is null || arn is null || store is null)
                return;

            instances.Add(new IdentityCenterInstance(region, arn, store));
            region = arn = store = null;
        }

        foreach (string raw in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string name = line[..split].Trim();
            string value = line[(split + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (name)
            {
                case "region":
                    Flush();
                    region = value;
                    break;
                case "instanceArn": arn = value; break;
                case "identityStoreId": store = value; break;
                case "missingPermission": missing.Add(value); break;
                case "accountType":
                    // An unrecognised value stays Unknown, which is the honest reading: a newer
                    // script talking to an older Connapse should not have its answer guessed at.
                    posture = value.ToLowerInvariant() switch
                    {
                        "standalone" => AwsAccountPosture.Standalone,
                        "member" => AwsAccountPosture.Member,
                        "management" => AwsAccountPosture.Management,
                        _ => AwsAccountPosture.Unknown
                    };
                    break;
            }
        }

        Flush();

        return new IdentityCenterSetupResult(instances, missing, posture);
    }
}
