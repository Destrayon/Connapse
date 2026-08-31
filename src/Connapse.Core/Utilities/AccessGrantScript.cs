namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the command that grants a directory group read access to a connection's buckets.
/// </summary>
/// <remarks>
/// Here rather than on the providers page because a grant names a bucket, and a bucket is a
/// property of a connection. The global setup cannot know which buckets will exist, which is why
/// its version of this command carried a placeholder where the bucket belonged — and was run with
/// the placeholder still in it.
/// <para>
/// Connapse does not run it. Every write in this setup is a command an administrator reads and runs
/// with their own credentials, and a grant is the one that most needs to stay that way: it is an
/// access-control decision, and Connapse's own identity deliberately has no permission to make it.
/// </para>
/// </remarks>
public static class AccessGrantScript
{
    /// <summary>The S3 Control actions the administrator running it needs.</summary>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "s3:ListAccessGrantsLocations",
        "s3:ListAccessGrants",
        "s3:CreateAccessGrant",
        "sts:GetCallerIdentity"
    ];

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <param name="region">Where the Access Grants instance lives.</param>
    /// <param name="allowedLocations">
    /// The connection's buckets, each optionally followed by <c>/</c> and a prefix. One grant is
    /// created per entry, because AWS refuses a grant covering every bucket in a region.
    /// </param>
    /// <param name="granteeId">The directory group or user the grant is for.</param>
    /// <param name="isGroup">
    /// True for a directory group. Groups are the shape worth using — membership then carries
    /// joining and leaving — but a user grant is a legitimate way to try one out.
    /// </param>
    /// <remarks>
    /// Safe to run again. Existing grants for this grantee are read first and skipped, so a
    /// second run reports what is already there and creates nothing. The conflict branch below
    /// stays as a backstop for the race between reading and creating.
    /// <para>
    /// The location id is discovered rather than assumed. <c>default</c> is what AWS calls the
    /// <c>s3://</c> location and is very often right, but a deployment that registered a bucket
    /// instead has a generated id, and a wrong one fails as <c>InvalidAccessGrant</c> — which reads
    /// as a bad grant rather than as a bad location.
    /// </para>
    /// </remarks>
    public static string GenerateScript(
        string? region, IEnumerable<string>? allowedLocations, string? granteeId, bool isGroup)
    {
        string pinnedRegion = AccessGrantsSetup.SanitiseRegion(region);
        string grantee = DirectoryGroupSetup.SanitiseId(granteeId);
        string granteeType = isGroup ? "DIRECTORY_GROUP" : "DIRECTORY_USER";

        // Each entry becomes one grant. A trailing star makes it a subtree; AWS refuses a grant on
        // the bare s3:// location, so every one of these has to name a bucket.
        var subPrefixes = (allowedLocations ?? [])
            .Select(SanitiseLocation)
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();

        string prefixList = string.Join(" ", subPrefixes.Select(l => $"'{l}/*'"));

        return $$"""
        # Grants read access to this connection's buckets. Run it when you are ready — Connapse
        # never creates a grant itself, and its own identity has no permission to.

        FAILED=""
        REGION="{{pinnedRegion}}"
        GRANTEE="{{grantee}}"
        GRANTEE_TYPE="{{granteeType}}"

        [ -n "$REGION" ] || { echo 'No region. Locate your Identity Center instance first.'; FAILED=1; }
        [ -n "$GRANTEE" ] || { echo 'No group chosen in Connapse to grant to.'; FAILED=1; }

        ACCOUNT=$(aws sts get-caller-identity --query Account --output text 2>/dev/null)
        [ -n "$ACCOUNT" ] || { echo 'Could not read your AWS account id.'; FAILED=1; }

        # Discovered, not assumed. AWS calls the s3:// location "default", but one registered
        # against a single bucket has a generated id, and naming the wrong one fails as
        # InvalidAccessGrant -- which reads as a bad grant rather than as a bad location.
        if [ -z "$FAILED" ]; then
          LOCATION=$(aws s3control list-access-grants-locations --region "$REGION" --account-id "$ACCOUNT" --query "AccessGrantsLocationsList[?LocationScope=='s3://'].AccessGrantsLocationId | [0]" --output text 2>/dev/null || true)
          if [ -z "$LOCATION" ] || [ "$LOCATION" = 'None' ]; then
            echo 'No s3:// location is registered in your Access Grants instance.'
            echo 'Run the Access Grants setup step in Connapse first.'
            FAILED=1
          fi
        fi

        # An array rather than a bare list: with one entry a quoted word reads as a command being
        # run, which is both what ShellCheck says and how somebody skimming this would read it.
        SUBPREFIXES=({{prefixList}})

        # What this grantee already has, read once. AWS documents no error for creating a grant
        # that already exists and says nothing about duplicates, so a second run might conflict --
        # handled below -- or might quietly make an identical second grant. A duplicate changes
        # nothing about what anyone can read, because Connapse dedupes scopes, but it doubles what
        # has to be deleted to revoke. Checking first makes the outcome the same either way.
        if [ -z "$FAILED" ]; then
          EXISTING=$(aws s3control list-access-grants --region "$REGION" --account-id "$ACCOUNT" --grantee-type "$GRANTEE_TYPE" --grantee-identifier "$GRANTEE" --query 'AccessGrantsList[].GrantScope' --output text 2>/dev/null | tr '\t' ' ' || true)
        fi

        if [ -z "$FAILED" ]; then
          for SUBPREFIX in "${SUBPREFIXES[@]}"; do
            # Compared as whole words rather than by pattern: a grant scope ends in a star, which a
            # case pattern would read as a wildcard and match buckets nobody granted. Splitting on
            # whitespace is safe because the allowlist above admits no spaces.
            ALREADY=""
            for SCOPE in $EXISTING; do
              [ "$SCOPE" = "s3://$SUBPREFIX" ] && ALREADY=1
            done

            if [ -n "$ALREADY" ]; then
              echo "Already granted on $SUBPREFIX"
              continue
            fi

            # One grant per bucket. AWS refuses a grant on the bare s3:// location, which would
            # reach every bucket in the region, so there is no way to cover them all at once.
            if OUT=$(aws s3control create-access-grant --region "$REGION" --account-id "$ACCOUNT" --access-grants-location-id "$LOCATION" --access-grants-location-configuration S3SubPrefix="$SUBPREFIX" --permission READ --grantee GranteeType="$GRANTEE_TYPE",GranteeIdentifier="$GRANTEE" 2>&1); then
              echo "Granted READ on $SUBPREFIX"
            else
              case "$OUT" in
                *Conflict*|*already*) echo "Already granted on $SUBPREFIX" ;;
                *) echo "Could not grant on $SUBPREFIX: $OUT"; FAILED=1 ;;
              esac
            fi
          done
        fi

        if [ -n "$FAILED" ]; then
          echo
          echo 'Something above failed. Nothing was recorded in Connapse; fix it and run this again.'
        else
          echo
          echo 'Done. Connapse honours this within a minute -- the warning on the connection clears'
          echo 'and searches start returning these documents to anybody in the group.'
        fi
        """.Replace("\r\n", "\n");
    }

    /// <summary>
    /// Keeps an allowed location only if it is safe to put in a single-quoted shell word.
    /// </summary>
    /// <remarks>
    /// Rejected rather than escaped, and the trailing slash dropped so the star this appends does
    /// not produce a double slash — which AWS stores literally, giving a grant scope that matches
    /// nothing anybody will ever ask for.
    /// </remarks>
    public static string SanitiseLocation(string? location)
    {
        string trimmed = location?.Trim().TrimEnd('/') ?? string.Empty;

        if (trimmed.Length is 0 or > 512)
            return string.Empty;

        bool safe = trimmed.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');

        return safe ? trimmed : string.Empty;
    }
}
