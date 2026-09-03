using System.Text.Json;

namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the IAM policy that lets Connapse read S3, and the deployment snippet that lets it see
/// any credentials at all.
/// </summary>
/// <remarks>
/// Both are text an operator copies into their own files — an IAM policy into AWS, a volume into
/// their compose file. Connapse never applies either, and stores no credential: it reads whatever
/// <c>DefaultAWSCredentials</c> finds, which is the operator's arrangement to make.
/// </remarks>
public static class S3SetupPolicy
{
    /// <summary>
    /// The two actions Connapse actually performs against a source bucket.
    /// </summary>
    /// <remarks>
    /// <c>ListBucket</c> is a bucket-level action and <c>GetObject</c> an object-level one, which
    /// is why the generated policy has two statements with different resources rather than one
    /// with both actions. A single statement listing both against either resource silently fails:
    /// against the bucket ARN alone, every object read is denied.
    /// </remarks>
    public static readonly IReadOnlyList<string> ReadActions = ["s3:ListBucket", "s3:GetObject"];

    /// <summary>
    /// The two actions Connapse needs to offer a bucket <i>list</i> rather than a text box.
    /// </summary>
    /// <remarks>
    /// <c>ListAllMyBuckets</c> cannot be scoped: IAM only accepts <c>"Resource": "*"</c> for it, so
    /// it is either granted account-wide or not at all. It reveals bucket names, nothing inside
    /// them. <c>GetBucketLocation</c> can be scoped, and is separated from the listing statement
    /// because it does not understand the <c>s3:prefix</c> condition — folded in beside
    /// <c>ListBucket</c>, every prefixed grant would silently deny the region lookup that
    /// <c>S3Discovery</c> performs before its first read.
    /// </remarks>
    public static readonly IReadOnlyList<string> DiscoveryActions =
        ["s3:ListAllMyBuckets", "s3:GetBucketLocation"];

    /// <summary>
    /// What Connapse needs to answer "what may this person read", using its own identity rather
    /// than theirs.
    /// </summary>
    /// <remarks>
    /// All four are read-only, and none of them reads any object. Together they replace holding a
    /// per-user credential: rather than acting as somebody to discover their permissions, Connapse
    /// asks the directory about them and reads the grants held against them.
    /// <para>
    /// <b>Worth stating to an administrator rather than leaving in a policy.</b>
    /// <c>s3:ListAccessGrants</c> is not scoped to one user by the permission itself — it is an
    /// administrative read over the whole instance, so Connapse can enumerate everyone's grants and
    /// not only the signed-in caller's. That is a narrower blast radius than a per-user credential
    /// and a wider reach, and an administrator should meet that fact on the setup page rather than
    /// discover it in IAM.
    /// </para>
    /// <para>
    /// <c>GetUserId</c> runs once when somebody connects, turning the name an assertion carried
    /// into the identity store id grants are held against. <c>DescribeUser</c> and
    /// <c>ListGroupMembershipsForMember</c> run when scopes are resolved: the first is how a
    /// deleted or suspended person is noticed at all, since no credential remains to expire, and
    /// the second is the group expansion that <c>ListAccessGrants</c> does not do for a grantee
    /// filter.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> PermissionResolutionActions =
    [
        "s3:ListAccessGrants",
        "identitystore:GetUserId",
        "identitystore:DescribeUser",
        "identitystore:ListGroupMembershipsForMember"
    ];

    /// <summary>
    /// The complete grant for the identity Connapse creates for itself: read across every AWS
    /// storage service Connapse can read from, plus creating the S3 access grants that make a
    /// connection searchable per-user.
    /// </summary>
    /// <remarks>
    /// One policy, not a choice of several. The easy setup exists to remove decisions, and a scope
    /// picker put the hardest one back: an operator sizing an IAM grant against sources that do not
    /// exist yet. Anyone who wants a narrower credential can create one themselves and point
    /// Connapse at it — that path is untouched, and <see cref="ForBuckets"/> writes the policy for
    /// it.
    /// <para>
    /// Account-wide rather than per-bucket because the grant lives in AWS and the choice of bucket
    /// lives in Connapse. A per-bucket grant sends someone back to CloudShell for every source
    /// added afterwards, possibly someone who no longer has IAM rights, to widen a policy nobody
    /// re-reads. That produces a policy edited under pressure, not least privilege.
    /// </para>
    /// <para>
    /// What it costs, plainly: a leaked key reads every object in the account, any Connapse
    /// administrator can point a source at any bucket, and — the one authority beyond reading —
    /// the identity can create S3 access grants, so a compromise of it can grant a directory group
    /// read access to a bucket. That last is a deliberate reversal: creating grants used to be an
    /// administrator's decision run in AWS, and is now Connapse's, made knowingly to remove a
    /// CloudShell trip. Against all this — the identity writes no object, belongs to Connapse
    /// alone, and revoking it touches nobody else's access. The narrowing lives in each
    /// connection's allowed-locations list, which <c>ConnectorFactory</c> enforces on every read.
    /// </para>
    /// <para>
    /// S3 alone today because S3 is the only AWS storage <c>ConnectionProvider</c> names. Adding
    /// another AWS storage service means appending its statements to <see cref="StorageStatements"/>
    /// — and only once Connapse can actually read from it, since a grant the product cannot use is
    /// authority asked for and wasted.
    /// </para>
    /// <para>
    /// Read this before appending. What this method returns is written into an inline IAM policy
    /// once, when the identity is created, so widening it here does nothing for an identity that
    /// already exists. Every installation set up before that release keeps the narrower policy and
    /// fails the new service with AccessDenied, while a developer testing against a freshly created
    /// user sees it work. Deliberately not solved in advance: the fix is small when it is needed
    /// and speculative now. <c>iam:PutUserPolicy</c> replaces an inline policy of the same name, so
    /// re-applying <c>ConnapseRead</c> to the existing user updates it in place and leaves the
    /// access key untouched — no new key, nothing to paste back.
    /// </para>
    /// </remarks>
    public static string ForManagedIdentity() =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = StorageStatements
        }, PolicyJson);

    /// <summary>
    /// One sentence describing <see cref="ForManagedIdentity"/>, for the operator about to run it.
    /// </summary>
    public const string ManagedIdentitySummary =
        "reading every S3 bucket in the account and creating and removing S3 access grants, which "
        + "grant directory groups read access to buckets. The only things it changes are grants: it "
        + "writes no object and deletes no data.";

    /// <summary>
    /// Every statement in the managed identity's policy, one group per AWS storage service.
    /// </summary>
    /// <remarks>
    /// <c>ListAllMyBuckets</c> is separate because IAM accepts only <c>"Resource": "*"</c> for it —
    /// written against <c>arn:aws:s3:::*</c> the statement parses, attaches, and then denies the
    /// call it exists for. It reveals bucket names, nothing inside them, and it is what lets the
    /// connection form offer a list of buckets rather than asking someone to type one from memory.
    /// <para>
    /// <c>GetBucketLocation</c> sits with <c>ListBucket</c> rather than in the object statement
    /// because it is a bucket-level call; <c>S3Discovery</c> makes it to find a bucket's region
    /// before the first read.
    /// </para>
    /// </remarks>
    private static readonly object[] StorageStatements =
    [
        new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseFindBuckets",
            ["Effect"] = "Allow",
            ["Action"] = new[] { "s3:ListAllMyBuckets" },
            ["Resource"] = "*"
        },
        new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseInspectBuckets",
            ["Effect"] = "Allow",
            ["Action"] = new[] { "s3:ListBucket", "s3:GetBucketLocation" },
            ["Resource"] = "arn:aws:s3:::*"
        },
        new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseReadObjects",
            ["Effect"] = "Allow",
            ["Action"] = new[] { "s3:GetObject" },
            // The trailing /* is the whole difference between reading objects and reading nothing:
            // without it this names buckets, and every object read is denied.
            ["Resource"] = "arn:aws:s3:::*/*"
        },
        // Named at the Access Grants instances of this account. AWS documents that resource form
        // for these actions, and it is the one worth narrowing: it is where the identity both
        // enumerates what every grantee may read and creates new grants.
        //
        // CreateAccessGrant is the deliberate exception to this policy being read-only. Connapse
        // now creates the grants that make a connection's buckets searchable per-user rather than
        // printing a script for an administrator to run -- so a compromise of this identity can
        // create grants, which the operator is told in ManagedIdentitySummary. ListAccessGrants-
        // Locations finds the s3:// location a grant attaches to. The grant is still bounded to
        // the access-grants resource; nothing here touches an object.
        //
        // The region is a wildcard because this script runs before the Identity Center region is
        // known -- it is the step that mints the credential the later steps use. The account is
        // substituted by the script from sts:GetCallerIdentity.
        new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseManageGrants",
            ["Effect"] = "Allow",
            ["Action"] = new[]
            {
                "s3:ListAccessGrants",
                "s3:ListAccessGrantsLocations",
                "s3:CreateAccessGrant",
                // Delete + tag: Connapse tags every grant it creates and later removes the ones no
                // connection needs. ListTagsForResource reads a grant's tag back (ListAccessGrants
                // does not return tags), which is how cleanup proves a grant is Connapse's own
                // before deleting it.
                "s3:DeleteAccessGrant",
                "s3:TagResource",
                "s3:ListTagsForResource"
            },
            ["Resource"] = $"arn:aws:s3:*:{AccountPlaceholder}:access-grants/*"
        },
        // Resource "*", and deliberately not narrowed on a guess. AWS's own Identity Store policy
        // examples use "*" for these, and the service authorization reference does not state
        // whether they accept a resource. A wrong ARN here does not fail loudly: the calls return
        // AccessDenied, the resolver treats that as an outage and denies, and every search comes
        // back empty with nothing saying why. Narrow it once the reference confirms the form.
        new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseReadDirectory",
            ["Effect"] = "Allow",
            ["Action"] = PermissionResolutionActions.Where(a => a.StartsWith("identitystore:")).ToArray(),
            ["Resource"] = "*"
        }
    ];

    /// <summary>
    /// Stands in for the AWS account id until the script that runs the policy substitutes it.
    /// </summary>
    /// <remarks>
    /// The account is not known here — this class builds a document, and the only place the number
    /// exists is the shell session the administrator runs it in. Written as a placeholder rather
    /// than a wildcard so that a policy which somehow reaches AWS unsubstituted is refused for a
    /// malformed ARN, rather than quietly attaching as an account-wide grant.
    /// </remarks>
    public const string AccountPlaceholder = "__CONNAPSE_ACCOUNT_ID__";

    /// <summary>
    /// One policy document covering every allowed location.
    /// </summary>
    /// <param name="locations">
    /// Allowlist entries, each a bucket optionally followed by <c>/</c> and a prefix — the same
    /// form the connection's allowed-locations field holds.
    /// </param>
    /// <remarks>
    /// A single document, not several concatenated. Generating one per bucket and joining them
    /// produces two top-level JSON objects, which is not a policy at all: IAM rejects it outright,
    /// while the UI presented it as something to paste. Anyone allowing two buckets got output
    /// that could not work.
    /// <para>
    /// Sids are suffixed by index because they must be unique within a document, and bucket names
    /// can contain characters a Sid cannot.
    /// </para>
    /// </remarks>
    public static string ForBuckets(IEnumerable<string> locations)
    {
        var statements = new List<object>();
        int index = 0;

        foreach (string location in locations)
        {
            if (string.IsNullOrWhiteSpace(location)) continue;

            string entry = location.Trim();
            int slash = entry.IndexOf('/');
            string bucket = (slash < 0 ? entry : entry[..slash]).Trim();
            if (bucket.Length == 0) continue;

            string prefix = NormalisePrefix(slash < 0 ? null : entry[(slash + 1)..]);

            var list = new Dictionary<string, object>
            {
                ["Sid"] = $"ConnapseListBucket{index}",
                ["Effect"] = "Allow",
                ["Action"] = new[] { "s3:ListBucket" },
                ["Resource"] = $"arn:aws:s3:::{bucket}"
            };

            if (prefix.Length > 0)
            {
                list["Condition"] = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object>
                    {
                        ["s3:prefix"] = new[] { $"{prefix}*" }
                    }
                };
            }

            statements.Add(new Dictionary<string, object>
            {
                ["Sid"] = $"ConnapseLocateBucket{index}",
                ["Effect"] = "Allow",
                ["Action"] = new[] { "s3:GetBucketLocation" },
                ["Resource"] = $"arn:aws:s3:::{bucket}"
            });
            statements.Add(list);
            statements.Add(new Dictionary<string, object>
            {
                ["Sid"] = $"ConnapseReadObjects{index}",
                ["Effect"] = "Allow",
                ["Action"] = new[] { "s3:GetObject" },
                ["Resource"] = $"arn:aws:s3:::{bucket}/{prefix}*"
            });

            index++;
        }

        if (statements.Count == 0)
            throw new ArgumentException("No usable bucket in the allowed locations.", nameof(locations));

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = statements
        }, PolicyJson);
    }

    /// <summary>
    /// Trims a prefix to the form an ARN wants: no leading slash, one trailing slash, or empty.
    /// </summary>
    /// <remarks>
    /// A prefix typed as <c>/docs</c> would produce <c>bucket//docs*</c>, which matches nothing —
    /// S3 keys have no leading slash. One typed as <c>docs</c> without a trailing slash would also
    /// match <c>docs-archive/</c>, quietly widening the grant past the folder the operator meant.
    /// </remarks>
    public static string NormalisePrefix(string? prefix)
    {
        string trimmed = (prefix ?? string.Empty).Trim().TrimStart('/');

        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    /// <summary>
    /// The compose fragment that lets the container see the operator's AWS credentials.
    /// </summary>
    /// <remarks>
    /// A read-only bind mount rather than environment variables: credentials stay off the
    /// container filesystem and out of its process listing, and the same mount carries whichever
    /// posture the operator chose — a static profile, an SSO profile, or a
    /// <c>credential_process</c> line pointing at the IAM Roles Anywhere helper, which is how a
    /// workload outside AWS gets temporary credentials with no static keys at all.
    /// <para>
    /// Connapse runs as a non-root user in its image, so the container-side path is that user's
    /// home rather than <c>/root</c>.
    /// </para>
    /// </remarks>
    /// <summary>The folder, beside docker-compose.yml, that is already mounted at ~/.aws.</summary>
    public const string CredentialFolder = "aws";

    /// <summary>The .env variable that points the mount somewhere else instead.</summary>
    public const string CredentialDirVariable = "CONNAPSE_AWS_DIR";

    /// <summary>
    /// What to do when Connapse can see no credentials.
    /// </summary>
    /// <remarks>
    /// Two steps, neither of which touches YAML. The compose file already mounts
    /// <c>./aws</c> read-only at the container user's <c>~/.aws</c>, so supplying credentials is
    /// dropping a file into a folder — which is the whole point: an operator who has to edit
    /// compose to make a feature work will conclude the feature does not work.
    /// <para>
    /// The mount is unconditional and defaults to a repo-relative path deliberately. A
    /// <c>${HOME}</c>-based default is blank in Windows PowerShell and silently mounts
    /// <c>/.aws</c>, and some Compose versions refuse to start when a bind source is missing —
    /// which a home directory without <c>.aws</c> very often is.
    /// </para>
    /// </remarks>
    public static string CredentialInstructions(string containerHome = "/home/app") =>
        $"""
        1. Put your AWS credentials file in the "{CredentialFolder}" folder beside
           docker-compose.yml. It is already mounted read-only at {containerHome}/.aws,
           and nothing is copied into Connapse.

           Or, to use a profile you already keep elsewhere, add one line to .env:

               {CredentialDirVariable}=C:\Users\you\.aws     (Windows)
               {CredentialDirVariable}=/home/you/.aws        (Linux, macOS)

        2. Restart, then run this check again:

               docker compose restart web
        """;

    private static readonly JsonSerializerOptions PolicyJson = new() { WriteIndented = true };
}
