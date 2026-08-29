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
        TimeSpan? sinceCreated = null,
        IProviderCredentialStore? credentials = null,
        CognitoSettings? cognito = null,
        IdentityCenterSettings? identityCenter = null)
    {
        var discovery = Substitute.For<IS3Discovery>();
        discovery.WhoAmIAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(identity);
        discovery.ListBucketsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(buckets);

        if (credentials is null)
        {
            credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetAsync("aws", Arg.Any<CancellationToken>()).Returns(stored);
        }

        var connections = Substitute.For<IConnectionStore>();
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return new ProviderSetupReader(
            Options.Create(new AzureAdSettings()).AsMonitor(),
            Options.Create(cognito ?? new CognitoSettings()).AsMonitor(),
            // Defaults to located, so a test that varies one thing is not also silently varying
            // this one. The tests that care pass an empty instance explicitly.
            Options.Create(identityCenter ?? LocatedInstance()).AsMonitor(),
            discovery, connections, credentials,
            new FixedClock(new DateTimeOffset(Created) + (sinceCreated ?? TimeSpan.Zero)),
            NullLogger<ProviderSetupReader>.Instance);
    }

    private static ProviderCredentialInfo StoredKey(DateTime? verifiedAt = null) =>
        new("aws", "AKIAEXAMPLE", "connapse-reader", Created, verifiedAt);

    /// <summary>A key that has been seen working, which is what rules propagation delay out.</summary>
    private static ProviderCredentialInfo VerifiedKey() => StoredKey(Created.AddMinutes(1));

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
    public async Task Access_KeyThatWorkedAndStopped_FailsImmediatelyRatherThanWaiting()
    {
        // Deleting the IAM user in AWS put the page into "Provisioning" -- it was young, so age
        // alone said wait. A credential that has already worked is not waiting to start working,
        // and offering to keep waiting for one that no longer exists is the page stalling somebody.
        var access = await AccessAsync(Build(
            AwsProbe<AwsCallerIdentity>.NoCredentials(),
            AwsProbe<IReadOnlyList<string>>.NoCredentials(),
            VerifiedKey(),
            sinceCreated: TimeSpan.FromMinutes(2)));

        access.Status.Should().Be(RequirementStatus.Failed);
    }

    [Fact]
    public async Task Access_KeyThatWorkedAndStopped_SaysItWasProbablyDeleted()
    {
        // "AWS has not finished issuing this key" is actively misleading here: it describes a wait
        // that will never end, for a key nothing will bring back.
        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey),
            AwsProbe<IReadOnlyList<string>>.Denied("AccessDenied"),
            VerifiedKey(),
            sinceCreated: TimeSpan.FromMinutes(2)));

        access.Detail.Should().Contain("worked before").And.Contain("deleted");
    }

    [Fact]
    public async Task Access_WhenS3Answers_RecordsThatTheKeyWorked()
    {
        // Nothing else is positioned to notice. Without this the distinction above has no input,
        // and every failure looks like a slow start.
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAsync("aws", Arg.Any<CancellationToken>()).Returns(StoredKey());

        await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey), Buckets("docs"), StoredKey(),
            credentials: credentials));

        await credentials.Received(1)
            .MarkVerifiedAsync("aws", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Access_WithNoStoredCredential_RecordsNothing()
    {
        // There is no row to mark, and an ambient credential is not Connapse's to track.
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAsync("aws", Arg.Any<CancellationToken>()).Returns((ProviderCredentialInfo?)null);

        await AccessAsync(Build(
            Authenticated(AwsCredentialKind.InstanceOrTaskRole), Buckets("docs"),
            credentials: credentials));

        await credentials.DidNotReceive()
            .MarkVerifiedAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Access_WhenRecordingTheSuccessFails_StillReportsReady()
    {
        // Losing the timestamp costs a wrong message on some later failure. Losing the status page
        // costs every message on it.
        var credentials = Substitute.For<IProviderCredentialStore>();
        credentials.GetAsync("aws", Arg.Any<CancellationToken>()).Returns(StoredKey());
        credentials.MarkVerifiedAsync("aws", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("database is down"));

        var access = await AccessAsync(Build(
            Authenticated(AwsCredentialKind.StoredKey), Buckets("docs"),
            credentials: credentials));

        access.Status.Should().Be(RequirementStatus.Satisfied);
    }

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
    public async Task InUse_FindsAProviderPastTheFirstPage()
    {
        // ListAsync pages. A single call read the first 200 connections and stopped, and the answer
        // is a boolean per provider -- so one S3 connection sorting past the cutoff was enough to
        // report AWS as unused and hide its requirements behind an invitation to set it up.
        var connections = Substitute.For<IConnectionStore>();

        var filler = Enumerable.Range(0, 200)
            .Select(i => new Connection(Guid.NewGuid(), $"sftp-{i}", ConnectionProvider.Sftp,
                null, null, Created, Created))
            .ToList();

        IReadOnlyList<Connection> secondPage =
        [
            new Connection(Guid.NewGuid(), "the-s3-one", ConnectionProvider.S3,
                null, null, Created, Created)
        ];

        // Answered from the skip the reader actually passes. Stubbing the two pages as separate
        // calls mixes a literal with argument matchers, which NSubstitute does not match on, so
        // both stubs are ignored and every page comes back null.
        connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(0) == 0 ? filler : secondPage);

        var reader = new ProviderSetupReader(
            Options.Create(new AzureAdSettings()).AsMonitor(),
            Options.Create(new CognitoSettings()).AsMonitor(),
            Options.Create(new IdentityCenterSettings()).AsMonitor(),
            Substitute.For<IS3Discovery>(), connections,
            Substitute.For<IProviderCredentialStore>(),
            new FixedClock(new DateTimeOffset(Created)),
            NullLogger<ProviderSetupReader>.Instance);

        var aws = (await reader.ReadAsync()).Single(p => p.Key == "aws");

        aws.InUse.Should().BeTrue();
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

    // ── Per-user permissions ──────────────────────────────────────────

    private static CognitoSettings ConfiguredPool() => new()
    {
        IssuerUrl = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_abc123",
        Domain = "https://pool.auth.us-east-1.amazoncognito.com",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        Region = "us-east-1",
        ApplicationArn = "arn:aws:sso::1:application/ssoins-1/apl-1",
        IdentityProvider = "Workforce"
    };

    private static IdentityCenterSettings LocatedInstance() => new()
    {
        Region = "us-east-1",
        InstanceArn = "arn:aws:sso:::instance/ssoins-1234567890abcdef",
        IdentityStoreId = "d-996773e796"
    };

    private static async Task<ProviderRequirement> IdentityCentreAsync(ProviderSetupReader reader) =>
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Requirements.Single(r => r.Name == "IAM Identity Center");

    private static async Task<ProviderRequirement> PermissionsAsync(ProviderSetupReader reader) =>
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Requirements.Single(r => r.Name == "Per-user permissions");

    [Fact]
    public async Task PerUserPermissions_WithNoPool_IsNotConfigured()
    {
        // Plainly NotConfigured, not a softened Warning. No part of a pool exists, and the card
        // that renders this says so; keeping the provider's own summary out of "Not set up" is
        // ProviderSetup.Overall's job, not this requirement's.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"));

        var requirement = await PermissionsAsync(reader);

        requirement.Status.Should().Be(RequirementStatus.NotConfigured);
        requirement.ActionHref.Should().Be("#permissions");
    }

    [Fact]
    public async Task PerUserPermissions_WithAPool_IsSatisfiedAndNamesIt()
    {
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            cognito: ConfiguredPool());

        var requirement = await PermissionsAsync(reader);

        requirement.Status.Should().Be(RequirementStatus.Satisfied);
        requirement.Detail.Should().Be(ConfiguredPool().IssuerUrl);
    }

    [Fact]
    public async Task PerUserPermissions_WithNoPool_StopsAwsClaimingItIsFullySetUp()
    {
        // The point of the requirement. Without it the provider list showed AWS as Ready while the
        // page below it plainly had an unconfigured section on it.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"));

        var aws = (await reader.ReadAsync()).Single(p => p.Key == "aws");

        aws.Requirements.Single(r => r.Name == "Access").Status
            .Should().Be(RequirementStatus.Satisfied);
        aws.Overall.Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public async Task PerUserPermissions_WithAPool_LetsAwsBeReady()
    {
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            cognito: ConfiguredPool());

        var aws = (await reader.ReadAsync()).Single(p => p.Key == "aws");

        aws.Overall.Should().Be(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task PerUserPermissions_WithoutTheApplicationArn_IsNotConfigured()
    {
        // Every other field can be present and the pool still cannot answer what anyone may read:
        // with no Identity Center application there is nothing to exchange a token with. It is a
        // sign-in that resolves to nobody, so it is not a configured pool.
        var unresolvable = ConfiguredPool();
        unresolvable.ApplicationArn = string.Empty;

        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            cognito: unresolvable);

        (await PermissionsAsync(reader)).Status.Should().Be(RequirementStatus.NotConfigured);
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Overall.Should().NotBe(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task IdentityCentre_WhenNotLocated_IsNotConfiguredAndAwsIsNotReady()
    {
        // Its own requirement because it is answered first and separately, and because the Cognito
        // script needs its region: Identity Center lives in exactly one region per organisation and
        // looking in the wrong one reads as there being no instance at all.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            cognito: ConfiguredPool(), identityCenter: new IdentityCenterSettings());

        (await IdentityCentreAsync(reader)).Status.Should().Be(RequirementStatus.NotConfigured);
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Overall.Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public async Task IdentityCentre_WhenLocated_NamesTheStoreAndRegion()
    {
        // The region is the field people get wrong, so it is the one worth showing back.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            cognito: ConfiguredPool());

        var requirement = await IdentityCentreAsync(reader);

        requirement.Status.Should().Be(RequirementStatus.Satisfied);
        requirement.Detail.Should().Be("d-996773e796 in us-east-1");
    }

    [Fact]
    public async Task PerUserPermissions_WithAHalfFilledPool_Warns()
    {
        // IsConfigured is the gate, not "somebody typed something". A pool missing its domain
        // cannot complete a connection, and reporting it green would send the administrator to
        // debug the Profile page instead of the field they left blank.
        var half = ConfiguredPool();
        half.Domain = string.Empty;

        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"), cognito: half);

        (await PermissionsAsync(reader)).Status.Should().Be(RequirementStatus.NotConfigured);
    }

    [Fact]
    public void Overall_UnconfiguredAlongsideSatisfied_IsPartlySetUpRatherThanUnconfigured()
    {
        var setup = new ProviderSetup("aws", "AWS",
        [
            new ProviderRequirement("Access", "", RequirementStatus.Satisfied),
            new ProviderRequirement("Per-user permissions", "", RequirementStatus.NotConfigured)
        ]);

        setup.Overall.Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public void Overall_UnconfiguredWithNothingSatisfied_StaysUnconfigured()
    {
        // The distinction only earns its keep in one direction. With nothing set up, "Not set up"
        // is exactly right and softening it would invent progress.
        var setup = new ProviderSetup("azure", "Azure",
        [
            new ProviderRequirement("Sign-in", "", RequirementStatus.NotConfigured),
            new ProviderRequirement("Access", "", RequirementStatus.Unknown)
        ]);

        setup.Overall.Should().Be(RequirementStatus.NotConfigured);
    }

    [Fact]
    public void Overall_FailedStillOutranksAPartlyConfiguredProvider()
    {
        var setup = new ProviderSetup("aws", "AWS",
        [
            new ProviderRequirement("Access", "", RequirementStatus.Satisfied),
            new ProviderRequirement("Sign-in", "", RequirementStatus.NotConfigured),
            new ProviderRequirement("Other", "", RequirementStatus.Failed)
        ]);

        setup.Overall.Should().Be(RequirementStatus.Failed);
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
