using System.Text.Json;

namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the IAM policy that lets Connapse read one bucket, and the deployment snippet that lets
/// it see any credentials at all.
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
    /// A policy granting read access to <paramref name="bucket"/>, narrowed to
    /// <paramref name="prefix"/> when one is given.
    /// </summary>
    /// <remarks>
    /// Offered so an operator can tighten credentials that currently allow more, which is the
    /// usual state of an access key someone made to get started. It is deliberately not the
    /// policy Connapse needs to <i>discover</i> buckets — that needs
    /// <c>s3:ListAllMyBuckets</c> across every bucket, and handing out a wide grant to save one
    /// dropdown is the wrong trade once the bucket is known.
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
