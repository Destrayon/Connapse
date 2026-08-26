namespace Connapse.Core.Interfaces;

/// <summary>
/// How the credentials Connapse resolved were obtained, which is the part that decides whether a
/// deployment is sound — not whether they work today.
/// </summary>
/// <remarks>
/// The SDK's default provider chain will resolve a static key from 2019 as happily as an instance
/// role and say nothing about the difference. Reporting the kind is what lets the UI say something.
/// </remarks>
public enum AwsCredentialKind
{
    /// <summary>Resolved, but the chain link is one this build does not recognise.</summary>
    Unrecognised = 0,

    /// <summary>An IAM role attached to the compute — EC2, ECS or EKS. Rotates itself, expires,
    /// belongs to the machine. The strongest option, and the only one needing no arrangement.</summary>
    InstanceOrTaskRole = 1,

    /// <summary>Assumed through STS, so temporary and scoped.</summary>
    AssumedRole = 2,

    /// <summary>An IAM Identity Center session. Temporary, but expires and needs a human with a
    /// browser to renew — so it cannot sustain unattended sync.</summary>
    SsoSession = 3,

    /// <summary>Produced by an external <c>credential_process</c>, which is how IAM Roles Anywhere
    /// is wired in: temporary credentials for a workload outside AWS, no static keys.</summary>
    ExternalProcess = 4,

    /// <summary>A long-lived access key, from the environment or a profile. Works, never expires,
    /// and is the thing AWS guidance tells people to move away from.</summary>
    StaticKey = 5
}

/// <summary>What the container could see and reach.</summary>
/// <param name="Arn">The identity the credentials resolve to, from <c>sts:GetCallerIdentity</c>.</param>
/// <param name="AccountId">The AWS account those credentials belong to.</param>
/// <param name="Kind">Where they came from.</param>
public record AwsCallerIdentity(string Arn, string AccountId, AwsCredentialKind Kind);

/// <summary>
/// The outcome of asking AWS something, separating the three cases that need different advice:
/// it worked, the credentials are absent, or they are present but not allowed.
/// </summary>
/// <remarks>
/// Collapsing "no credentials" into "denied" is the specific failure this exists to prevent. The
/// first is a deployment problem the operator fixes in their compose file; the second is an IAM
/// problem they fix in AWS. The messages have nothing in common.
/// </remarks>
public record AwsProbe<T>(T? Value, AwsProbeOutcome Outcome, string? Detail = null)
{
    public bool Succeeded => Outcome == AwsProbeOutcome.Succeeded;

    public static AwsProbe<T> Ok(T value) => new(value, AwsProbeOutcome.Succeeded);

    public static AwsProbe<T> NoCredentials(string? detail = null) =>
        new(default, AwsProbeOutcome.NoCredentials, detail);

    public static AwsProbe<T> Denied(string? detail = null) =>
        new(default, AwsProbeOutcome.Denied, detail);

    public static AwsProbe<T> Failed(string? detail = null) =>
        new(default, AwsProbeOutcome.Failed, detail);
}

public enum AwsProbeOutcome
{
    Succeeded = 0,

    /// <summary>Nothing in the chain resolved. The container cannot see any credentials.</summary>
    NoCredentials = 1,

    /// <summary>Credentials resolved, and AWS refused the call.</summary>
    Denied = 2,

    /// <summary>Something else — a timeout, a network fault, an unexpected error.</summary>
    Failed = 3
}

/// <summary>
/// Reads what the container's own AWS credentials can see, so the connection form can be filled in
/// rather than typed.
/// </summary>
/// <remarks>
/// Unlike the SFTP and identity-provider wizards, this one needs no script: Connapse's process is
/// meant to hold these credentials, so it can make the calls itself.
/// </remarks>
public interface IS3Discovery
{
    /// <summary>Who Connapse is, as far as AWS is concerned.</summary>
    Task<AwsProbe<AwsCallerIdentity>> WhoAmIAsync(CancellationToken ct = default);

    /// <summary>
    /// Every bucket the credentials can enumerate.
    /// </summary>
    /// <remarks>
    /// Needs <c>s3:ListAllMyBuckets</c>, which narrowly scoped credentials very often lack — and
    /// lacking it is correct, not broken. A denial here must leave the operator typing a bucket
    /// name, never blocked.
    /// </remarks>
    Task<AwsProbe<IReadOnlyList<string>>> ListBucketsAsync(CancellationToken ct = default);

    /// <summary>The region a bucket lives in, so it does not have to be guessed.</summary>
    Task<AwsProbe<string>> GetBucketRegionAsync(string bucket, CancellationToken ct = default);
}
