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
/// <summary>How much of S3 the identity Connapse creates for itself is allowed to read.</summary>
public enum S3AccessScope
{
    /// <summary>Every bucket in the account. The default; see <see cref="S3SetupPolicy.ForAllBuckets"/>.</summary>
    AllBuckets,

    /// <summary>Buckets whose name matches a pattern, such as <c>acme-docs-*</c>.</summary>
    NamePattern,

    /// <summary>One named bucket, optionally one folder within it.</summary>
    OneBucket
}

/// <summary>A policy document and the one-line description shown beside it.</summary>
/// <param name="Policy">The IAM policy JSON.</param>
/// <param name="Summary">
/// What it allows, in the operator's words rather than IAM's — this is what someone reads before
/// deciding to run a script that creates a credential.
/// </param>
/// <param name="CanDiscoverBuckets">
/// Whether the grant includes <c>s3:ListAllMyBuckets</c>, which decides whether the connection form
/// can offer a list of buckets or has to ask for a name.
/// </param>
public record S3AccessGrant(string Policy, string Summary, bool CanDiscoverBuckets);

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
    /// A read-only policy over every bucket in the account.
    /// </summary>
    /// <remarks>
    /// The default the UI offers, after weighing it against naming buckets one at a time.
    /// <para>
    /// A per-bucket grant is tighter on paper and worse in practice. The grant is written in AWS
    /// and the bucket is chosen in Connapse, so the two drift the moment anyone adds a source:
    /// every new bucket means another CloudShell round trip, by someone who may no longer have IAM
    /// rights, to widen a policy nobody re-reads. What that produces is not least privilege but a
    /// policy edited under pressure, and an operator who reaches for their own admin key because
    /// the supported path was too slow.
    /// </para>
    /// <para>
    /// There is no working middle ground below this. Tag-based scoping — the obvious answer — needs
    /// ABAC turned on per bucket before <c>aws:ResourceTag</c> applies to one, which is more manual
    /// work than typing the name. What is left is a name pattern, offered as
    /// <see cref="ForBucketPattern"/>.
    /// </para>
    /// <para>
    /// What the wide grant costs is real and worth stating plainly: a leaked key reads every object
    /// in the account, and any Connapse administrator can point a source at any bucket, which moves
    /// that decision out of IAM and into this application. The mitigation is that the identity is
    /// read-only, belongs to Connapse alone, and can be revoked without touching anyone's own
    /// access.
    /// </para>
    /// </remarks>
    public static string ForAllBuckets() => ForBucketPattern("*");

    /// <summary>
    /// A read-only policy over the buckets whose names match <paramref name="pattern"/>.
    /// </summary>
    /// <param name="pattern">
    /// A bucket name with <c>*</c> and <c>?</c> allowed, such as <c>acme-docs-*</c>. Characters a
    /// bucket name cannot contain are dropped rather than rejected, so a half-typed pattern renders
    /// a policy instead of an error page — the generated policy is shown in full, so the effective
    /// pattern is visible before anyone runs it.
    /// </param>
    /// <remarks>
    /// The middle ground between one bucket and all of them, and the only one AWS supports without
    /// per-bucket setup. It holds only while bucket names follow a convention; when they do, new
    /// buckets land inside the existing grant and need no second CloudShell visit.
    /// </remarks>
    public static string ForBucketPattern(string? pattern)
    {
        string clean = NormaliseBucketPattern(pattern);

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new object[]
            {
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
                    ["Resource"] = $"arn:aws:s3:::{clean}"
                },
                new Dictionary<string, object>
                {
                    ["Sid"] = "ConnapseReadObjects",
                    ["Effect"] = "Allow",
                    ["Action"] = new[] { "s3:GetObject" },
                    ["Resource"] = $"arn:aws:s3:::{clean}/*"
                }
            }
        }, PolicyJson);
    }

    /// <summary>
    /// Reduces a typed pattern to characters a bucket name allows, plus the two wildcards.
    /// </summary>
    /// <remarks>
    /// A slash would turn a bucket pattern into a key pattern and silently grant nothing, since the
    /// bucket-level statement's resource has no key part to match.
    /// </remarks>
    public static string NormaliseBucketPattern(string? pattern)
    {
        string cleaned = new string((pattern ?? string.Empty).Trim()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '*' or '?')
            .ToArray());

        return cleaned.Length == 0 ? "*" : cleaned;
    }

    /// <summary>
    /// A policy granting read access to <paramref name="bucket"/>, narrowed to
    /// <paramref name="prefix"/> when one is given.
    /// </summary>
    /// <remarks>
    /// The tightest of the three scopes, for a deployment that reads one bucket and is expected to
    /// keep reading only that one. It cannot discover buckets — <c>s3:ListAllMyBuckets</c> is not
    /// scopeable — so the connection form asks for a bucket name rather than offering a list.
    /// <para>
    /// Not the default. Every additional bucket needs this policy rewritten in AWS, and
    /// <see cref="ForAllBuckets"/> explains why that trade usually goes the other way.
    /// </para>
    /// </remarks>
    public static string ForBucket(string bucket, string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        string cleanBucket = bucket.Trim();
        string cleanPrefix = NormalisePrefix(prefix);

        // The object statement is scoped by key, the bucket statement by a ListBucket condition.
        // Without the condition the grant still bounds *reading* to the prefix, but lets the
        // holder enumerate every key in the bucket, which is more than Connapse needs.
        string objectResource = $"arn:aws:s3:::{cleanBucket}/{cleanPrefix}*";

        var listStatement = new Dictionary<string, object>
        {
            ["Sid"] = "ConnapseListBucket",
            ["Effect"] = "Allow",
            ["Action"] = new[] { "s3:ListBucket" },
            ["Resource"] = $"arn:aws:s3:::{cleanBucket}"
        };

        // Added rather than set to null. A dictionary entry whose value is null still serialises,
        // and IAM rejects "Condition": null outright — WhenWritingNull governs object properties,
        // not dictionary entries, so the key has to be absent instead of empty.
        if (cleanPrefix.Length > 0)
        {
            listStatement["Condition"] = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object>
                {
                    ["s3:prefix"] = new[] { $"{cleanPrefix}*" }
                }
            };
        }

        var policy = new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new object[]
            {
                // Its own statement, unconditioned. GetBucketLocation does not understand
                // s3:prefix, so folding it in beside ListBucket would deny the region lookup on
                // every grant that names a folder.
                new Dictionary<string, object>
                {
                    ["Sid"] = "ConnapseLocateBucket",
                    ["Effect"] = "Allow",
                    ["Action"] = new[] { "s3:GetBucketLocation" },
                    ["Resource"] = $"arn:aws:s3:::{cleanBucket}"
                },
                listStatement,
                new Dictionary<string, object>
                {
                    ["Sid"] = "ConnapseReadObjects",
                    ["Effect"] = "Allow",
                    ["Action"] = new[] { "s3:GetObject" },
                    ["Resource"] = objectResource
                }
            }
        };

        return JsonSerializer.Serialize(policy, PolicyJson);
    }

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

    /// <summary>
    /// The policy for a chosen scope, with the sentence describing it.
    /// </summary>
    /// <param name="scope">Which of the three shapes the operator picked.</param>
    /// <param name="bucketOrPattern">
    /// The bucket name for <see cref="S3AccessScope.OneBucket"/>, the pattern for
    /// <see cref="S3AccessScope.NamePattern"/>, ignored otherwise.
    /// </param>
    /// <param name="prefix">A folder within the bucket; only meaningful for a single bucket.</param>
    /// <remarks>
    /// Falls back to the widest scope when a narrower one is chosen without the text it needs, so
    /// the page renders a working script while someone is still typing. That is safe in the
    /// direction it matters: the script is shown in full and the summary says what it allows, so
    /// nobody creates a wider credential than they read about.
    /// </remarks>
    public static S3AccessGrant Grant(S3AccessScope scope, string? bucketOrPattern, string? prefix = null)
    {
        switch (scope)
        {
            case S3AccessScope.NamePattern when !string.IsNullOrWhiteSpace(bucketOrPattern):
            {
                string pattern = NormaliseBucketPattern(bucketOrPattern);
                return new S3AccessGrant(
                    ForBucketPattern(pattern),
                    $"reading buckets named {pattern}, and listing the names of the rest.",
                    CanDiscoverBuckets: true);
            }

            case S3AccessScope.OneBucket when !string.IsNullOrWhiteSpace(bucketOrPattern):
            {
                string bucket = bucketOrPattern.Trim();
                string folder = NormalisePrefix(prefix);
                return new S3AccessGrant(
                    ForBucket(bucket, prefix),
                    folder.Length > 0
                        ? $"reading {folder} in {bucket}, and nothing else."
                        : $"reading {bucket}, and nothing else.",
                    CanDiscoverBuckets: false);
            }

            default:
                return new S3AccessGrant(
                    ForAllBuckets(),
                    "reading every bucket in the account. It cannot write, delete, or change anything.",
                    CanDiscoverBuckets: true);
        }
    }

    private static readonly JsonSerializerOptions PolicyJson = new() { WriteIndented = true };
}
