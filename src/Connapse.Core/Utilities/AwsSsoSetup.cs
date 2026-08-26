namespace Connapse.Core.Utilities;

/// <summary>
/// One IAM Identity Center instance the setup script found.
/// </summary>
/// <param name="Region">
/// The region whose <c>sso-admin list-instances</c> returned it. The field people most often get
/// wrong, and the one that cannot be derived from a legacy portal URL.
/// </param>
/// <param name="InstanceArn"><c>arn:aws:sso:::instance/ssoins-…</c>.</param>
/// <param name="IdentityStoreId">
/// <c>d-…</c>. Also the subdomain of the default access portal URL, which is why the portal URL
/// can be derived rather than asked for.
/// </param>
/// <param name="PortalUrl">
/// The access portal URL, used as the issuer URL when registering the OIDC client.
/// <para>
/// Derived from <paramref name="IdentityStoreId"/>, so it is a default rather than an answer: an
/// organisation using a custom vanity domain has a different one, and only they know it.
/// </para>
/// </param>
public record AwsSsoInstance(
    string Region,
    string InstanceArn,
    string IdentityStoreId,
    string PortalUrl);

/// <summary>
/// Where the account sits relative to AWS Organizations, which decides whether an instance can be
/// created and what kind it would be.
/// </summary>
public enum AwsAccountPosture
{
    /// <summary>The script could not tell — usually no permission to call DescribeOrganization.</summary>
    Unknown = 0,

    /// <summary>
    /// Not in an organisation. <c>CreateInstance</c> works here and produces the only instance the
    /// account will have, which is unambiguously the right one.
    /// </summary>
    Standalone = 1,

    /// <summary>
    /// A member account inside an organisation. <c>CreateInstance</c> is permitted but produces an
    /// <i>account</i> instance: no multi-account permissions, no organisation-wide application
    /// assignment, no multi-region replication. Almost always the organisation already has an
    /// instance and this would be a weaker parallel one.
    /// </summary>
    Member = 2,

    /// <summary>
    /// The Organizations management account. <c>CreateInstance</c> is rejected outright here — an
    /// organisation instance is enabled through the console, where the primary region and the
    /// encryption key are chosen.
    /// </summary>
    Management = 3
}

/// <summary>
/// What the setup script reported.
/// </summary>
/// <param name="Instances">
/// Every instance found, in the order the script printed them. Usually one — Identity Center is a
/// single-region service per organisation — but multi-region replication gives one per region,
/// and the administrator has to pick, because Connapse cannot know which their users sign in
/// through.
/// </param>
/// <param name="MissingPermissions">
/// Actions the caller was denied. Reported rather than inferred from an empty result, because
/// "no permission to look" and "looked, found nothing" need different advice and produce the
/// same empty list otherwise.
/// </param>
/// <param name="Posture">
/// Only meaningful when <paramref name="Instances"/> is empty, which is the one case where what to
/// do next depends on it.
/// </param>
public record AwsSsoSetupResult(
    IReadOnlyList<AwsSsoInstance> Instances,
    IReadOnlyList<string>? MissingPermissions = null,
    AwsAccountPosture Posture = AwsAccountPosture.Unknown)
{
    public IReadOnlyList<string> MissingPermissions { get; init; } = MissingPermissions ?? [];

    /// <summary>
    /// Whether <see cref="AwsSsoSetup.GenerateCreateScript"/> would be accepted by AWS. Says
    /// nothing about whether it is a good idea — for a member account it is permitted and usually
    /// wrong, which is a judgement only the administrator can make.
    /// </summary>
    public bool CanCreateInstance =>
        Posture is AwsAccountPosture.Standalone or AwsAccountPosture.Member;
}

/// <summary>
/// Builds the one command an administrator runs in AWS CloudShell to locate their IAM Identity
/// Center instance, and reads back what it reports.
/// <para>
/// CloudShell rather than a credential field or a CloudFormation callback. It already holds
/// temporary credentials from the administrator's console session, so nothing is created, pasted
/// into Connapse, or stored — the copy-paste round trip <i>is</i> the credential boundary. A
/// CloudFormation custom resource calling back into Connapse, which is how most services onboard
/// an AWS account, cannot work for a self-hosted product that frequently has no public URL.
/// </para>
/// </summary>
public static class AwsSsoSetup
{
    /// <summary>
    /// Delimits the block the administrator pastes back. Deliberately unmistakable, because the
    /// usual failure is pasting the whole terminal buffer, and that should still work.
    /// </summary>
    public const string BeginMarker = "----- BEGIN CONNAPSE AWS SETUP -----";

    public const string EndMarker = "----- END CONNAPSE AWS SETUP -----";

    /// <summary>
    /// Regions the scan tries when the CloudShell session's own region has no instance.
    /// </summary>
    /// <remarks>
    /// Not every AWS region — that would be a long serial scan for a service that exists in
    /// exactly one place. These are the commercial regions Identity Center is commonly deployed
    /// in, ordered roughly by how often that is true. A miss is not fatal: the script says it
    /// scanned and found nothing, and the administrator can read their region off the console.
    /// <para>
    /// GovCloud and the China partitions are absent on purpose. They need different endpoints and
    /// a different partition in the ARN, so a caller there needs a different script, not a longer
    /// list.
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
        "sso:ListInstances"
    ];

    /// <summary>
    /// The command to paste into AWS CloudShell.
    /// </summary>
    /// <remarks>
    /// Meant to be read before it is run. It is read-only — it lists Identity Center instances and
    /// prints what it found — but it is shown in full rather than offered as a download or a pipe
    /// from a URL, so the administrator can see exactly what they are agreeing to at the moment
    /// they agree to it.
    /// <para>
    /// Targets CloudShell specifically, which is Amazon Linux with <c>bash</c> and the AWS CLI
    /// already present and authenticated. It deliberately does not use <c>jq</c>: CloudShell ships
    /// it, but the CLI's own <c>--query</c> does the same job without depending on that.
    /// </para>
    /// </remarks>
    public static string GenerateScript()
    {
        string regions = string.Join(" ", CandidateRegions);

        return $$"""
        # Finds your IAM Identity Center instance. Read-only: it lists instances and prints
        # what it found. Nothing is created, changed, or sent anywhere.

        FOUND=""
        DENIED=""

        probe() {
          # --output text with an explicit --query, rather than jq: CloudShell ships jq, but
          # depending on it buys nothing the CLI cannot already do.
          OUT=$(aws sso-admin list-instances \
                  --region "$1" \
                  --query 'Instances[].[InstanceArn,IdentityStoreId]' \
                  --output text 2>&1)
          STATUS=$?

          if [ $STATUS -ne 0 ]; then
            # A denial is worth reporting; anything else is this region simply not being the
            # one, which is the expected outcome for all but one of them.
            case "$OUT" in
              *AccessDenied*|*UnauthorizedOperation*|*not\ authorized*)
                DENIED="yes" ;;
            esac
            return
          fi

          [ -z "$OUT" ] && return

          # Tab-separated, one instance per line. Held rather than printed so that the whole
          # result block comes out together at the end.
          while IFS=$(printf '\t') read -r ARN STORE; do
            [ -z "$ARN" ] && continue
            FOUND="${FOUND}${1}|${ARN}|${STORE}
        "
          done <<EOF
        $OUT
        EOF
        }

        # The session's own region first. For most people this is the answer and the scan
        # below never runs.
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
            # replication, which the administrator will know they have -- so stopping at the
            # first hit is right for everyone else, and a full scan would be 20 sequential
            # calls for nothing.
            [ -n "$FOUND" ] && break
          done
        fi

        # Where this account sits relative to Organizations. Only consulted when nothing was
        # found, but probed unconditionally so the answer travels in the same block.
        #
        # A denial has to be told apart from "not in an organisation": both leave the query
        # empty, and treating a denial as standalone would offer a create step that AWS rejects.
        ORG_OUT=$(aws organizations describe-organization \
                    --query 'Organization.MasterAccountId' --output text 2>&1)
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

        # The markers go through %s rather than being the format string themselves. They begin
        # with dashes, and printf reads a leading '-' as an option.
        printf '\n%s\n' '{{BeginMarker}}'
        printf 'accountType=%s\n' "$POSTURE"

        if [ -n "$FOUND" ]; then
          printf '%s' "$FOUND" | while IFS='|' read -r REGION ARN STORE; do
            [ -z "$REGION" ] && continue
            printf 'region=%s\n' "$REGION"
            printf 'instanceArn=%s\n' "$ARN"
            printf 'identityStoreId=%s\n' "$STORE"
            # The default access portal URL is the identity store id as a subdomain. An
            # organisation with a custom vanity domain has a different one, so Connapse offers
            # this as a starting value rather than a fact.
            printf 'portalUrl=https://%s.awsapps.com/start\n' "$STORE"
          done
        elif [ -n "$DENIED" ]; then
          printf 'missingPermission=%s\n' 'sso:ListInstances'
        fi

        printf '%s\n\n' '{{EndMarker}}'
        echo 'Copy the block above, including both marker lines, back into Connapse.'
        """;
    }

    /// <summary>
    /// The command that creates an Identity Center instance, for an administrator who has decided
    /// they want one.
    /// </summary>
    /// <param name="name">
    /// The instance name. Constrained by AWS to <c>[\w+=,.@-]+</c>, so anything else is replaced
    /// rather than passed through to fail in the shell.
    /// </param>
    /// <remarks>
    /// Deliberately a second script rather than a branch inside <see cref="GenerateScript"/>. That
    /// one is offered as read-only and the UI says so; folding a create into it would make the
    /// claim false for everyone, including the people who only ever wanted to look.
    /// <para>
    /// It re-checks the posture itself instead of trusting what the first script reported. The two
    /// runs are separated by however long the administrator spent reading, and the second is the
    /// one that writes.
    /// </para>
    /// <para>
    /// This creates an <i>account</i> instance. In the Organizations management account AWS rejects
    /// the call outright: an organisation instance is enabled through the console, which is where
    /// the primary region — unchangeable afterwards — and the encryption key are chosen.
    /// </para>
    /// </remarks>
    public static string GenerateCreateScript(string? name = null)
    {
        string safe = SanitiseInstanceName(name);

        return $$"""
        # Creates an IAM Identity Center instance in THIS account. Unlike the previous command,
        # this one makes a change.

        ORG_OUT=$(aws organizations describe-organization \
                    --query 'Organization.MasterAccountId' --output text 2>&1)
        CALLER=$(aws sts get-caller-identity --query 'Account' --output text 2>/dev/null)

        if [ "$ORG_OUT" = "$CALLER" ] && [ -n "$CALLER" ]; then
          echo 'This is your AWS Organizations management account.'
          echo 'AWS rejects CreateInstance here. Enable an organization instance from the console:'
          echo '  https://console.aws.amazon.com/singlesignon/home'
          echo 'You will choose a primary region, which CANNOT be changed afterwards.'
          exit 1
        fi

        case "$ORG_OUT" in
          *AWSOrganizationsNotInUse*) ;;
          *)
            # Permitted, and usually not what they want. Said plainly rather than blocked: the
            # administrator knows their organisation and Connapse does not.
            echo 'NOTE: this account belongs to an AWS Organization.'
            echo 'What gets created is an ACCOUNT instance: no multi-account permissions, no'
            echo 'organization-wide application assignment, no multi-region replication. If your'
            echo 'organization already has an instance, this will be a separate, weaker one and'
            echo 'your users will not be in it.'
            echo ''
            echo 'Press Ctrl-C now to stop, or wait 10 seconds to continue.'
            sleep 10 ;;
        esac

        OUT=$(aws sso-admin create-instance --name '{{safe}}' \
                --query 'InstanceArn' --output text 2>&1)

        if [ $? -ne 0 ]; then
          echo "Could not create the instance:"
          printf '%s\n' "$OUT"
          exit 1
        fi

        printf 'Created %s\n' "$OUT"
        echo 'Now run the first command again to read it back into Connapse.'
        """;
    }

    /// <summary>
    /// Coerces an instance name to the character set AWS accepts, so a name with a space in it
    /// fails validation here rather than inside the administrator's shell.
    /// </summary>
    public static string SanitiseInstanceName(string? name)
    {
        const string fallback = "Connapse";

        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var cleaned = new string(name.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '=' or ',' or '.' or '@' or '-'
                ? c
                : '-')
            .ToArray());

        // 255 is the documented maximum. An all-punctuation name collapses to dashes, which is
        // valid but meaningless, so an empty-after-trim result falls back too.
        cleaned = cleaned.Trim('-');

        return cleaned.Length == 0 ? fallback : cleaned[..Math.Min(cleaned.Length, 255)];
    }

    /// <summary>
    /// Reads the block the setup command printed. Returns null when the text does not contain a
    /// usable one.
    /// </summary>
    /// <remarks>
    /// Tolerant on purpose. Administrators paste whole terminal buffers, with prompts, wrapping,
    /// and the command echoed above the output — so this finds the markers rather than expecting
    /// the text to begin at them, and ignores anything outside.
    /// <para>
    /// A block containing neither an instance nor a denial still parses, returning an empty
    /// result. That is a real outcome — the scan ran and found nothing — and it needs to reach
    /// the caller as such rather than as "you pasted the wrong thing".
    /// </para>
    /// </remarks>
    public static AwsSsoSetupResult? ParseResult(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        int start = pasted.IndexOf(BeginMarker, StringComparison.Ordinal);
        int end = pasted.IndexOf(EndMarker, StringComparison.Ordinal);

        // Without both markers there is nothing to be confident about. Guessing at loose
        // key=value lines would happily accept a paste from somewhere else entirely.
        if (start < 0 || end < 0 || end <= start)
            return null;

        string body = pasted[(start + BeginMarker.Length)..end];

        var instances = new List<AwsSsoInstance>();
        var missing = new List<string>();
        var posture = AwsAccountPosture.Unknown;

        string? region = null, arn = null, store = null, portal = null;

        // region starts each record, so meeting one again means the previous record is done.
        void Flush()
        {
            if (region is null || arn is null || store is null)
                return;

            instances.Add(new AwsSsoInstance(
                region, arn, store,
                portal ?? $"https://{store}.awsapps.com/start"));

            region = arn = store = portal = null;
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
                case "portalUrl": portal = value; break;
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

        return new AwsSsoSetupResult(instances, missing, posture);
    }
}
