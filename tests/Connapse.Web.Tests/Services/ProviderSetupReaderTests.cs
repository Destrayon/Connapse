using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Connapse.Web.Tests.Services;

/// <summary>
/// The AWS access requirement: ready, coming up, or not.
/// </summary>
/// <remarks>
/// Two faults shipped here and both were invisible to a compiler. The check reported success from
/// <c>sts:GetCallerIdentity</c> alone, which IAM does not evaluate against policy, so an identity
/// whose grant never attached showed a green tick. And a freshly created key is routinely refused
/// for a while, so the honest failure state had to be split from the one that only looks like it.
/// </remarks>
[Trait("Category", "Unit")]
public class ProviderSetupReaderTests
{
    private const string Arn = "arn:aws:iam::086015909943:user/connapse-reader";

    /// <summary>A clock that answers whatever the test needs, so the hour does not have to pass.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTime Created = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static ProviderSetupReader Build(
        AwsProbe<AwsCallerIdentity> identity,
        AwsProbe<IReadOnlyList<string>> buckets,
        ProviderCredentialInfo? stored = null,
        TimeSpan? sinceCreated = null)
    {
        var discovery = Substitute.For<IS3Discovery>();
        discovery.WhoAmIAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(identity);
        discovery.ListBucketsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(buckets);

        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAsync("aws", Arg.Any<CancellationToken>()).Returns(stored);

        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return new ProviderSetupReader(
            Options.Create(new AwsSsoSettings()).AsMonitor(),
            Options.Create(new AzureAdSettings()).AsMonitor(),
            discovery, connections, credentials,
            new FixedClock(new DateTimeOffset(Created) + (sinceCreated ?? TimeSpan.Zero)),
            NullLogger<ProviderSetupReader>.Instance);
    }

    private static ProviderCredentialInfo StoredKey() =>
        new("aws", "AKIAEXAMPLE", "connapse-reader", Created);

    private static async Task<ProviderRequirement> AccessAsync(ProviderSetupReader reader) =>
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Requirements.Single(r => r.Name == "Access");

    private static AwsProbe<AwsCallerIdentity> Authenticated(AwsCredentialKind kind) =>
        AwsProbe<AwsCallerIdentity>.Ok(new AwsCallerIdentity(Arn, "086015909943", kind));

    private static AwsProbe<IReadOnlyList<string>> Buckets(params string[] names) =>
        AwsProbe<IReadOnlyList<string>>.Ok(names);

    [Fact]
    public async Task Access_WhenS3Answers_IsReady()
    {
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey), Buckets("docs"), StoredKey()));

        access.Status.Should().Be(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task Access_WhenReady_SaysOnlyWhichIdentity()
    {
        // The detail is the ARN, because that is the fact needed when the answer is no. Bucket
        // counts and a description of where the key came from were narration on a yes/no question.
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey), Buckets("a", "b"), StoredKey()));

        access.Detail.Should().Be(Arn);
    }

    [Fact]
    public async Task Access_StoredKeyThatAuthenticatesButCannotReadS3_IsNotReady()
    {
        // The fault this exists for. GetCallerIdentity answers for any valid credential whatever
        // its policy allows, so an identity whose inline policy never attached passed the old check
        // outright and failed at the first sync instead.
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey),
            AwsProbe<IReadOnlyList<string>>.Denied("AccessDenied"),
            StoredKey()));

        access.Status.Should().NotBe(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task Access_StoredKeyStillWithinTheWindow_IsProvisioning()
    {
        // IAM is eventually consistent, and this window is exactly when the administrator who just
        // created the key is looking at the page. Reporting a failure sends them to redo work that
        // was about to succeed on its own.
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey),
            AwsProbe<IReadOnlyList<string>>.Denied("AccessDenied"),
            StoredKey(),
            sinceCreated: TimeSpan.FromMinutes(2)));

        access.Status.Should().Be(RequirementStatus.Provisioning);
    }

    [Fact]
    public async Task Access_StoredKeyPastTheWindow_Fails()
    {
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey),
            AwsProbe<IReadOnlyList<string>>.Denied("AccessDenied"),
            StoredKey(),
            sinceCreated: ProviderSetupReader.ProvisioningWindow + TimeSpan.FromMinutes(1)));

        access.Status.Should().Be(RequirementStatus.Failed);
        access.ActionHref.Should().NotBeNull("a failure the administrator caused needs a way back");
    }

    [Fact]
    public async Task Access_StoredKeyNotAcceptedAtAll_FollowsTheSameWindow()
    {
        // A key AWS has not started honouring fails one step earlier, at GetCallerIdentity. Same
        // cause, so the same patience -- treating this one as an outright fault would report a
        // failure for the commonest kind of propagation delay.
        var early = await AccessAsync(Build(
            AwsProbe<AwsCallerIdentity>.NoCredentials(),
            AwsProbe<IReadOnlyList<string>>.NoCredentials(),
            StoredKey(),
            sinceCreated: TimeSpan.FromMinutes(2)));

        var late = await AccessAsync(Build(
            AwsProbe<AwsCallerIdentity>.NoCredentials(),
            AwsProbe<IReadOnlyList<string>>.NoCredentials(),
            StoredKey(),
            sinceCreated: ProvisioningWindowPlus));

        early.Status.Should().Be(RequirementStatus.Provisioning);
        late.Status.Should().Be(RequirementStatus.Failed);
    }

    private static readonly TimeSpan ProvisioningWindowPlus =
        ProviderSetupReader.ProvisioningWindow + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Access_AmbientCredentialThatCannotListBuckets_IsNotCalledAFailure()
    {
        // A credential Connapse did not create is not Connapse's to judge. One an operator scoped
        // to named buckets lacks s3:ListAllMyBuckets by design and syncs perfectly; calling that
        // broken would be wrong about a working installation, and there is nothing to re-provision.
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StaticKey),
            AwsProbe<IReadOnlyList<string>>.Denied("AccessDenied"),
            stored: null,
            sinceCreated: TimeSpan.FromDays(30)));

        access.Status.Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public async Task Access_NoCredentialsAndNoneStored_IsNotSetUpRatherThanFailed()
    {
        // A fresh install has a next step, not a fault, and colouring it red says otherwise.
        var access = await AccessAsync(Build(
            AwsProbe<AwsCallerIdentity>.NoCredentials(),
            AwsProbe<IReadOnlyList<string>>.NoCredentials()));

        access.Status.Should().Be(RequirementStatus.NotConfigured);
    }

    [Fact]
    public void Overall_TakesTheWorstRequirement_AndFailedIsTheWorst()
    {
        var setup = new ProviderSetup("aws", "AWS",
        [
            new ProviderRequirement("Sign-in", "", RequirementStatus.Satisfied),
            new ProviderRequirement("Access", "", RequirementStatus.Failed)
        ]);

        setup.Overall.Should().Be(RequirementStatus.Failed);
    }

    [Fact]
    public void Overall_ProvisioningOutranksWarning()
    {
        // One is unfinished, the other is finished and merely imperfect.
        var setup = new ProviderSetup("aws", "AWS",
        [
            new ProviderRequirement("Sign-in", "", RequirementStatus.Warning),
            new ProviderRequirement("Access", "", RequirementStatus.Provisioning)
        ]);

        setup.Overall.Should().Be(RequirementStatus.Provisioning);
    }
}

internal static class OptionsMonitorExtensions
{
    /// <summary>Wraps a fixed value as an IOptionsMonitor, which is all these settings need here.</summary>
    public static IOptionsMonitor<T> AsMonitor<T>(this IOptions<T> options) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(options.Value);
        return monitor;
    }
}
