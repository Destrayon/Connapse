using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Asks AWS what this container's own credentials can see.
/// </summary>
/// <remarks>
/// Every call goes through the SDK's default provider chain — no credential is passed in, and none
/// is stored. That chain is what makes the same code pick up an instance role in production and a
/// mounted profile in development, which is why Connapse uses it rather than holding credentials
/// of its own.
/// <para>
/// What the chain will not tell you is whether the credential it found is a good one: a static key
/// resolves exactly as smoothly as an instance role. Classifying the result is the point of
/// <see cref="Classify"/>.
/// </para>
/// </remarks>
public class S3Discovery(
    ConnapseAwsCredentials credentials,
    ILogger<S3Discovery> logger) : IS3Discovery
{
    /// <summary>
    /// Endpoint for the calls that are not about a particular bucket.
    /// </summary>
    /// <remarks>
    /// STS and ListBuckets answer the same everywhere, so this is only somewhere to send the
    /// request. It is not a claim about where anything lives.
    /// </remarks>
    private static readonly RegionEndpoint DiscoveryRegion = RegionEndpoint.USEast1;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Key this provider's credential is stored under.</summary>
    public const string AwsProviderKey = "aws";

    public async Task<AwsProbe<AwsCallerIdentity>> WhoAmIAsync(
        string? roleArn = null, CancellationToken ct = default)
    {
        var (baseCredentials, error) = Resolve();
        if (baseCredentials is null)
            return AwsProbe<AwsCallerIdentity>.NoCredentials(error
                ?? "The AWS SDK found no credentials in its provider chain.");

        try
        {
            AWSCredentials credentials = await AssumeIfNeededAsync(baseCredentials, roleArn, ct);

            using var sts = new AmazonSecurityTokenServiceClient(credentials,
                new AmazonSecurityTokenServiceConfig { RegionEndpoint = DiscoveryRegion, Timeout = Timeout });

            var response = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);

            // Kind describes the BASE credentials deliberately. The assumed role is temporary by
            // construction, so classifying the session would report every deployment as healthy
            // and hide the static key underneath it.
            return AwsProbe<AwsCallerIdentity>.Ok(new AwsCallerIdentity(
                response.Arn, response.Account, Classify(baseCredentials),
                string.IsNullOrWhiteSpace(roleArn) ? null : roleArn.Trim()));
        }
        catch (AmazonServiceException ex) when (IsDenial(ex))
        {
            // Rare but real: credentials that exist and are refused sts:GetCallerIdentity, which
            // almost every policy allows. Worth distinguishing anyway, because "your key is wrong"
            // and "your key is not allowed to do this" send the operator to different places.
            logger.LogWarning(ex, "GetCallerIdentity was denied");
            return AwsProbe<AwsCallerIdentity>.Denied(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetCallerIdentity failed");
            return AwsProbe<AwsCallerIdentity>.Failed(ex.Message);
        }
    }

    public async Task<AwsProbe<IReadOnlyList<string>>> ListBucketsAsync(
        string? roleArn = null, CancellationToken ct = default)
    {
        var (baseCredentials, error) = Resolve();
        if (baseCredentials is null)
            return AwsProbe<IReadOnlyList<string>>.NoCredentials(error);

        try
        {
            AWSCredentials credentials = await AssumeIfNeededAsync(baseCredentials, roleArn, ct);

            using var s3 = new AmazonS3Client(credentials,
                new AmazonS3Config { RegionEndpoint = DiscoveryRegion, Timeout = Timeout });

            var response = await s3.ListBucketsAsync(new ListBucketsRequest(), ct);

            IReadOnlyList<string> names = response.Buckets?
                .Select(b => b.BucketName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            return AwsProbe<IReadOnlyList<string>>.Ok(names);
        }
        catch (AmazonS3Exception ex) when (IsDenial(ex))
        {
            // Expected, and not a fault. s3:ListAllMyBuckets is account-wide, so a credential
            // scoped to one bucket is *supposed* to be refused here. The caller falls back to
            // asking for the name.
            logger.LogDebug("ListBuckets denied — credentials are scoped, which is fine");
            return AwsProbe<IReadOnlyList<string>>.Denied(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ListBuckets failed");
            return AwsProbe<IReadOnlyList<string>>.Failed(ex.Message);
        }
    }

    public async Task<AwsProbe<string>> GetBucketRegionAsync(
        string bucket, string? roleArn = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            return AwsProbe<string>.Failed("No bucket name given.");

        var (baseCredentials, error) = Resolve();
        if (baseCredentials is null)
            return AwsProbe<string>.NoCredentials(error);

        try
        {
            AWSCredentials credentials = await AssumeIfNeededAsync(baseCredentials, roleArn, ct);

            using var s3 = new AmazonS3Client(credentials,
                new AmazonS3Config { RegionEndpoint = DiscoveryRegion, Timeout = Timeout });

            var response = await s3.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = bucket.Trim() }, ct);

            // The oldest quirk in S3: us-east-1 reports itself as an empty location constraint,
            // because it predates the constraint existing. Passing that through would put a blank
            // in the region field for the single most common region.
            string region = response.Location?.Value is { Length: > 0 } value
                ? value
                : "us-east-1";

            // And "EU" is a legacy alias that RegionEndpoint does not accept.
            if (region.Equals("EU", StringComparison.OrdinalIgnoreCase))
                region = "eu-west-1";

            return AwsProbe<string>.Ok(region);
        }
        catch (AmazonS3Exception ex) when (IsDenial(ex))
        {
            logger.LogDebug("GetBucketLocation denied for {Bucket}", bucket);
            return AwsProbe<string>.Denied(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetBucketLocation failed for {Bucket}", bucket);
            return AwsProbe<string>.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Assumes <paramref name="roleArn"/> when the connection names one, so every probe runs as
    /// the principal the sync will actually run as.
    /// </summary>
    /// <remarks>
    /// <c>S3Connector</c> and <c>S3ConnectionTester</c> both assume the role when it is set.
    /// Discovery did not, so a connection configured for cross-account access reported the base
    /// identity, listed the base account's buckets, and read regions from the wrong place — then
    /// told the operator to attach the generated policy to that identity. The grant would land on
    /// a principal the sync never uses, and the setup would look correct and fail later.
    /// <para>
    /// The 900-second session matches the tester's. Discovery is a handful of calls made while
    /// somebody watches, so nothing here outlives it.
    /// </para>
    /// </remarks>
    private static async Task<AWSCredentials> AssumeIfNeededAsync(
        AWSCredentials baseCredentials, string? roleArn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roleArn))
            return baseCredentials;

        using var sts = new AmazonSecurityTokenServiceClient(baseCredentials,
            new AmazonSecurityTokenServiceConfig { RegionEndpoint = DiscoveryRegion, Timeout = Timeout });

        var assumed = await sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = roleArn.Trim(),
            RoleSessionName = "connapse-discovery",
            DurationSeconds = 900
        }, ct);

        return new SessionAWSCredentials(
            assumed.Credentials.AccessKeyId,
            assumed.Credentials.SecretAccessKey,
            assumed.Credentials.SessionToken);
    }

    /// <summary>
    /// The credential every AWS client in Connapse uses, or null with a reason.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ConnapseAwsCredentials"/> rather than resolving here. It is the one place
    /// the order is decided — configured identity first, environment second — so discovery, the
    /// connector and the connection tester cannot drift apart on what they run as, which they had
    /// already started to do.
    /// <para>
    /// Every exception is caught. Nothing this can hit is worth crashing a page over: callers treat
    /// null as "tell them how to supply a credential", which is the useful answer to any failure to
    /// obtain one. The narrower catch that preceded this took the Blazor circuit down on the most
    /// likely state of a fresh deployment.
    /// </para>
    /// </remarks>
    private (AWSCredentials? Credentials, string? Error) Resolve()
    {
        try
        {
            // Forces a resolve now, so a missing or unreadable credential is an answer here rather
            // than an exception from the first API call.
            credentials.GetCredentials();
            return (credentials, null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No AWS credentials available");
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Names the chain link the credentials came from.
    /// </summary>
    /// <remarks>
    /// By runtime type, which is the only thing the chain exposes about its own decision. The
    /// point is not diagnostics but posture: a static key works forever and is what AWS guidance
    /// steers people away from, and nothing else in the system would ever mention it.
    /// </remarks>
    internal static AwsCredentialKind Classify(AWSCredentials credentials) =>
        ClassifyType(credentials.GetType());

    /// <summary>
    /// The same decision, made from the type alone.
    /// </summary>
    /// <remarks>
    /// Split out because several of these classes do real work in their constructors and cannot be
    /// created in a test: <c>EnvironmentVariablesAWSCredentials</c> throws when the variables are
    /// unset, and <c>InstanceProfileAWSCredentials</c> calls the EC2 metadata service and takes
    /// most of a second to fail anywhere else. Taking the type keeps the one piece of judgement
    /// here testable without any of that.
    /// <para>
    /// Walks the base chain, so a derived or wrapped credential still classifies as the thing it
    /// derives from rather than falling through to Unrecognised.
    /// </para>
    /// </remarks>
    internal static AwsCredentialKind ClassifyType(Type? type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            AwsCredentialKind kind = t.Name switch
            {
                nameof(InstanceProfileAWSCredentials) => AwsCredentialKind.InstanceOrTaskRole,
                nameof(GenericContainerCredentials) => AwsCredentialKind.InstanceOrTaskRole,
                nameof(AssumeRoleWithWebIdentityCredentials) => AwsCredentialKind.AssumedRole,
                nameof(AssumeRoleAWSCredentials) => AwsCredentialKind.AssumedRole,
                nameof(SessionAWSCredentials) => AwsCredentialKind.AssumedRole,
                nameof(SSOAWSCredentials) => AwsCredentialKind.SsoSession,
                nameof(ProcessAWSCredentials) => AwsCredentialKind.ExternalProcess,
                nameof(EnvironmentVariablesAWSCredentials) => AwsCredentialKind.StaticKey,
                nameof(BasicAWSCredentials) => AwsCredentialKind.StaticKey,
                // Ours, and it has to be named here or it falls through: it derives from
                // RefreshingAWSCredentials, which nothing above matches, so the whole base chain
                // misses and the page reported a working identity as "source not recognised".
                nameof(ConnapseAwsCredentials) => AwsCredentialKind.StoredKey,
                _ => AwsCredentialKind.Unrecognised
            };

            if (kind != AwsCredentialKind.Unrecognised)
                return kind;
        }

        return AwsCredentialKind.Unrecognised;
    }

    private static bool IsDenial(AmazonServiceException ex) =>
        ex.StatusCode is System.Net.HttpStatusCode.Forbidden
                      or System.Net.HttpStatusCode.Unauthorized
        || (ex.ErrorCode?.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ?? false);
}
