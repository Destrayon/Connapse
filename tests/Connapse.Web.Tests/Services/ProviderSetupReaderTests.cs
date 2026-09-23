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
        ProviderCredentialStatus? stored = null,
        TimeSpan? sinceCreated = null,
        IProviderCredentialStore? credentials = null,
        SamlSignInSettings? samlSignIn = null,
        IdentityCenterSettings? identityCenter = null,
        AzureProviderSettings? azureProvider = null,
        AzureAdSignInSettings? azureAd = null,
        IConnectionStore? connections = null,
        AzureProbe<string>? azureAccess = null,
        AzureProbe<string>? azureSignIn = null,
        PermissionEnforcementSettings? enforcement = null)
    {
        var discovery = Substitute.For<IS3Discovery>();
        discovery.WhoAmIAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(identity);
        discovery.ListBucketsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(buckets);

        // Defaults to Entra accepting both credentials, so a test about anything else is not also
        // silently a test about the live checks. The sign-in check is told apart from the access
        // check by the candidate it is handed: the sign-in app's client id.
        var azureDiscovery = Substitute.For<IAzureBlobDiscovery>();
        azureDiscovery.CheckAccessAsync(Arg.Any<CancellationToken>())
            .Returns(azureAccess ?? AzureProbe<string>.Ok("Entra accepted the certificate."));
        azureDiscovery.CheckAccessAsync(Arg.Any<AzureProviderSettings>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<AzureProviderSettings>(0).ClientId == (azureAd ?? new AzureAdSignInSettings()).ClientId
                ? azureSignIn ?? AzureProbe<string>.Ok("Entra accepted the sign-in certificate.")
                : azureAccess ?? AzureProbe<string>.Ok("Entra accepted the certificate."));

        if (credentials is null)
        {
            credentials = Substitute.For<IProviderCredentialStore>();
            credentials.GetStatusAsync("aws", Arg.Any<CancellationToken>()).Returns(stored);
        }

        if (connections is null)
        {
            connections = Substitute.For<IConnectionStore>();
            connections.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([]);
        }

        return new ProviderSetupReader(
            Options.Create(samlSignIn ?? new SamlSignInSettings()).AsMonitor(),
            // Defaults to located, so a test that varies one thing is not also silently varying
            // this one. The tests that care pass an empty instance explicitly.
            Options.Create(identityCenter ?? LocatedInstance()).AsMonitor(),
            Options.Create(azureProvider ?? new AzureProviderSettings()).AsMonitor(),
            Options.Create(azureAd ?? new AzureAdSignInSettings()).AsMonitor(),
            discovery, azureDiscovery,
            // Latched by default: a sign-in test is about sign-in, not about the latch. The test
            // that cares passes the open state explicitly.
            Options.Create(enforcement ?? new PermissionEnforcementSettings { IsEnforcing = true, AzureEnforcing = true }).AsMonitor(),
            connections, credentials,
            new FixedClock(new DateTimeOffset(Created) + (sinceCreated ?? TimeSpan.Zero)),
            NullLogger<ProviderSetupReader>.Instance);
    }

    /// <summary>A stored credential's status: created at <see cref="Created"/>, verified when asked.</summary>
    private static ProviderCredentialStatus StoredKey(DateTime? verifiedAt = null) =>
        new(Created, verifiedAt);

    /// <summary>A credential that has been seen working, which is what rules propagation delay out.</summary>
    private static ProviderCredentialStatus VerifiedKey() => StoredKey(Created.AddMinutes(1));

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
        credentials.GetStatusAsync("aws", Arg.Any<CancellationToken>()).Returns(StoredKey());

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
        credentials.GetStatusAsync("aws", Arg.Any<CancellationToken>()).Returns((ProviderCredentialStatus?)null);

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
        credentials.GetStatusAsync("aws", Arg.Any<CancellationToken>()).Returns(StoredKey());
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
            Options.Create(new SamlSignInSettings()).AsMonitor(),
            Options.Create(new IdentityCenterSettings()).AsMonitor(),
            Options.Create(new AzureProviderSettings()).AsMonitor(),
            Options.Create(new AzureAdSignInSettings()).AsMonitor(),
            Substitute.For<IS3Discovery>(), Substitute.For<IAzureBlobDiscovery>(),
            Options.Create(new PermissionEnforcementSettings()).AsMonitor(), connections,
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

    private static SamlSignInSettings ConfiguredSignIn() => new()
    {
        EntityId = "https://connapse.example.com/saml/connapse",
        AcsUrl = "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
        IdpEntityId = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSingleSignOnUrl = "https://portal.sso.us-west-1.amazonaws.com/saml/assertion/EXAMPLE",
        IdpSigningCertificate = "MIIDBTCCAe2gAwIBAgIFEXAMPLE",
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
            samlSignIn: ConfiguredSignIn());

        var requirement = await PermissionsAsync(reader);

        requirement.Status.Should().Be(RequirementStatus.Satisfied);
        requirement.Detail.Should().Be(ConfiguredSignIn().EntityId);
    }

    [Fact]
    public async Task PerUserPermissions_WithNoSignIn_StopsAwsClaimingItIsFullySetUp()
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
            samlSignIn: ConfiguredSignIn());

        var aws = (await reader.ReadAsync()).Single(p => p.Key == "aws");

        aws.Overall.Should().Be(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task PerUserPermissions_WithoutTheApplicationArn_IsNotConfigured()
    {
        // Every other field can be present and the sign-in still cannot be trusted: with no
        // signing certificate there is nothing to validate an assertion against, so it would have
        // to be either refused late or believed unverified.
        var unresolvable = ConfiguredSignIn();
        unresolvable.IdpSigningCertificate = string.Empty;

        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            samlSignIn: unresolvable);

        (await PermissionsAsync(reader)).Status.Should().Be(RequirementStatus.NotConfigured);
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Overall.Should().NotBe(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task IdentityCentre_WhenNotLocated_IsNotConfiguredAndAwsIsNotReady()
    {
        // Its own requirement because it is answered first and separately, and because the sign-in
        // script needs its region: Identity Center lives in exactly one region per organisation and
        // looking in the wrong one reads as there being no instance at all.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            samlSignIn: ConfiguredSignIn(), identityCenter: new IdentityCenterSettings());

        (await IdentityCentreAsync(reader)).Status.Should().Be(RequirementStatus.NotConfigured);
        (await reader.ReadAsync()).Single(p => p.Key == "aws")
            .Overall.Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public async Task IdentityCentre_WhenLocated_NamesTheStoreAndRegion()
    {
        // The region is the field people get wrong, so it is the one worth showing back.
        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            samlSignIn: ConfiguredSignIn());

        var requirement = await IdentityCentreAsync(reader);

        requirement.Status.Should().Be(RequirementStatus.Satisfied);
        requirement.Detail.Should().Be("d-996773e796 in us-east-1");
    }

    [Fact]
    public async Task PerUserPermissions_WithAHalfFilledSignIn_Warns()
    {
        // IsConfigured is the gate, not "somebody typed something". A sign-in with no consumer
        // URL cannot complete, and reporting it green would send the administrator to debug the
        // Profile page instead of the field they left blank.
        var half = ConfiguredSignIn();
        half.AcsUrl = string.Empty;

        var reader = Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"), samlSignIn: half);

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

    // ── Azure provider (surfaced on the Providers page) ───────────────

    private static AzureProviderSettings ConfiguredAzureProvider() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        ClientCertificatePath = "/certs/azure.pem",
        SubscriptionId = "33333333-3333-3333-3333-333333333333",
    };

    private static AzureAdSignInSettings ConfiguredAzureAd() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "44444444-4444-4444-4444-444444444444",
        RedirectUri = "https://connapse.example.com/api/v1/auth/cloud/azure/callback",
        ClientCertificatePath = "/certs/azure-signin.pem",
    };

    private static async Task<ProviderSetup> AzureAsync(ProviderSetupReader reader) =>
        (await reader.ReadAsync()).Single(p => p.Key == "azure");

    private static IConnectionStore ConnectionsWith(params ConnectionProvider[] providers)
    {
        var store = Substitute.For<IConnectionStore>();
        IReadOnlyList<Connection> page = providers
            .Select(p => new Connection(Guid.NewGuid(), $"{p}", p, null, null, Created, Created))
            .ToList();
        // All-matcher stub with a callback: mixing a literal skip (0) with Arg matchers makes
        // NSubstitute ignore the stub entirely (every page then comes back null).
        store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(0) == 0 ? page : []);
        return store;
    }

    [Fact]
    public async Task Azure_IsListedAsAProvider()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one")));

        azure.DisplayName.Should().Be("Azure");
    }

    [Fact]
    public async Task Azure_WithNothingConfigured_IsNotInUseAndAllRequirementsNotConfigured()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one")));

        azure.InUse.Should().BeFalse();
        azure.Requirements.Should().OnlyContain(r => r.Status == RequirementStatus.NotConfigured);
    }

    [Fact]
    public async Task Azure_Access_WithTenantAndCredential_IsSatisfied()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider()));

        var access = azure.Requirements.Single(r => r.Name == "Access");
        access.Status.Should().Be(RequirementStatus.Satisfied);
        // A pass says what was verified, in the words the probe used.
        access.Detail.Should().Contain("Entra accepted");
    }

    [Fact]
    public async Task Azure_Access_WhenEntraRefusesTheCredential_IsFailed_AndSaysToSetUpAgain()
    {
        // An expired or unregistered certificate is the one thing settings alone cannot see, and
        // the reason to ask Entra at all. It must read as Failed, not Ready.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(),
            azureAccess: AzureProbe<string>.Denied("AADSTS700027: The certificate is not registered on application")));

        var access = azure.Requirements.Single(r => r.Name == "Access");
        access.Status.Should().Be(RequirementStatus.Failed);
        access.Detail.Should().Contain("set access up again").And.Contain("AADSTS700027");
    }

    [Fact]
    public async Task Azure_Access_WhenTheIdentityIsUnusableOnThisHost_IsFailed_AndNamesTheFile()
    {
        // A missing or unreadable certificate file is not "could not confirm": nothing was asked
        // of Azure and nothing will work until the file is fixed. Failed, with the remedy.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(),
            azureAccess: AzureProbe<string>.Unusable("no usable certificate was loaded (ClientCertificatePath='/certs/azure.pem')")));

        var access = azure.Requirements.Single(r => r.Name == "Access");
        access.Status.Should().Be(RequirementStatus.Failed);
        access.Detail.Should().Contain("cannot be used from this host").And.Contain("/certs/azure.pem");
    }

    [Fact]
    public async Task Azure_Access_WhenTheCheckCannotComplete_Warns_RatherThanFails()
    {
        // A network fault says nothing about the credential, so it is a warning: "cannot confirm",
        // which the connection test can still answer.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(),
            azureAccess: AzureProbe<string>.Failed("No such host is known")));

        var access = azure.Requirements.Single(r => r.Name == "Access");
        access.Status.Should().Be(RequirementStatus.Warning);
        access.Detail.Should().Contain("could not confirm").And.Contain("No such host");
    }

    [Fact]
    public async Task Azure_Access_WithNothingConfigured_NeverAsksEntra()
    {
        var azureDiscovery = Substitute.For<IAzureBlobDiscovery>();
        var reader = new ProviderSetupReader(
            Options.Create(new SamlSignInSettings()).AsMonitor(),
            Options.Create(new IdentityCenterSettings()).AsMonitor(),
            Options.Create(new AzureProviderSettings()).AsMonitor(),
            Options.Create(new AzureAdSignInSettings()).AsMonitor(),
            Substitute.For<IS3Discovery>(), azureDiscovery,
            Options.Create(new PermissionEnforcementSettings()).AsMonitor(), ConnectionsWith(),
            Substitute.For<IProviderCredentialStore>(),
            new FixedClock(new DateTimeOffset(Created)),
            NullLogger<ProviderSetupReader>.Instance);

        var azure = await AzureAsync(reader);

        azure.Requirements.Single(r => r.Name == "Access").Status.Should().Be(RequirementStatus.NotConfigured);
        await azureDiscovery.DidNotReceive().CheckAccessAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_PerUserPermissions_SignInAndSubscription_IsSatisfied()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(), azureAd: ConfiguredAzureAd()));

        azure.Requirements.Single(r => r.Name == "Per-user permissions").Status
            .Should().Be(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task Azure_PerUserPermissions_WhenEntraRefusesTheSignInCredential_IsFailed()
    {
        // An expired or removed sign-in certificate is invisible to settings, and a Ready card over
        // it would send people to a sign-in that fails. Failed, with the reason and the fix.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(), azureAd: ConfiguredAzureAd(),
            azureSignIn: AzureProbe<string>.Denied("AADSTS700027: Client assertion contains an invalid signature")));

        var requirement = azure.Requirements.Single(r => r.Name == "Per-user permissions");
        requirement.Status.Should().Be(RequirementStatus.Failed);
        requirement.Detail.Should().Contain("sign-in application").And.Contain("AADSTS700027");
        // The access credential was fine; only the sign-in one was refused.
        azure.Requirements.Single(r => r.Name == "Access").Status.Should().Be(RequirementStatus.Satisfied);
    }

    [Fact]
    public async Task Azure_PerUserPermissions_SignInSetButEnforcementOff_IsFailed_BecauseThatStateIsOpen()
    {
        // The one combination that returns every Azure result to everyone: sign-in configured, latch
        // off. It must never read as Ready, and the fix is named.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(), azureAd: ConfiguredAzureAd(),
            enforcement: new PermissionEnforcementSettings { IsEnforcing = true, AzureEnforcing = false }));

        var requirement = azure.Requirements.Single(r => r.Name == "Per-user permissions");
        requirement.Status.Should().Be(RequirementStatus.Failed);
        requirement.Detail.Should().Contain("not filtered").And.Contain("Save the sign-in application again");
    }

    [Fact]
    public async Task Azure_PerUserPermissions_WhenTheSignInCheckCannotComplete_Warns()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(), azureAd: ConfiguredAzureAd(),
            azureSignIn: AzureProbe<string>.Failed("No such host is known")));

        var requirement = azure.Requirements.Single(r => r.Name == "Per-user permissions");
        requirement.Status.Should().Be(RequirementStatus.Warning);
        requirement.Detail.Should().Contain("could not confirm");
    }

    [Fact]
    public void SignInIdentity_CarriesTheFourFieldsTheCredentialCheckNeeds()
    {
        var identity = ProviderSetupReader.SignInIdentity(ConfiguredAzureAd() with { ClientCertificatePassword = "pw" });

        identity.TenantId.Should().Be(ConfiguredAzureAd().TenantId);
        identity.ClientId.Should().Be(ConfiguredAzureAd().ClientId);
        identity.ClientCertificatePath.Should().Be(ConfiguredAzureAd().ClientCertificatePath);
        identity.ClientCertificatePassword.Should().Be("pw");
        identity.UserAssignedManagedIdentityClientId.Should().BeNull();
    }

    [Fact]
    public async Task Azure_PerUserPermissions_SignInButNoSubscription_Warns()
    {
        // The RBAC resolver needs the subscription, so sign-in alone fails closed at query time —
        // a warning, not a clean state.
        var noSub = ConfiguredAzureProvider() with { SubscriptionId = null };
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: noSub, azureAd: ConfiguredAzureAd()));

        azure.Requirements.Single(r => r.Name == "Per-user permissions").Status
            .Should().Be(RequirementStatus.Warning);
    }

    [Fact]
    public async Task Azure_PerUserPermissions_ConsentPending_Warns_NotSatisfied()
    {
        // The pending flag is stored with the sign-in settings, so a reload cannot turn a
        // consent-less setup into a green card.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider(), azureAd: ConfiguredAzureAd() with { AdminConsentPending = true }));

        var requirement = azure.Requirements.Single(r => r.Name == "Per-user permissions");
        requirement.Status.Should().Be(RequirementStatus.Warning);
        requirement.Detail.Should().Contain("consent");
    }

    [Fact]
    public async Task Azure_PerUserPermissions_WithoutSignIn_IsNotConfigured()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider()));

        azure.Requirements.Single(r => r.Name == "Per-user permissions").Status
            .Should().Be(RequirementStatus.NotConfigured);
    }

    [Fact]
    public async Task Azure_WithAnAzureBlobConnection_IsInUse()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            connections: ConnectionsWith(ConnectionProvider.AzureBlob)));

        azure.InUse.Should().BeTrue();
    }

    [Fact]
    public async Task Azure_WithAccessSavedButNoConnection_IsInUse_SoTheListShowsItsState()
    {
        // Saving the Access step is an explicit choice (Azure access is settings-only, nothing ambient
        // can make it read as configured), so the list must show status rather than an offer.
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureProvider: ConfiguredAzureProvider()));

        azure.InUse.Should().BeTrue();
    }

    [Fact]
    public async Task Aws_WithSignInConfiguredButNoConnection_IsInUse()
    {
        // The documented rule: sign-in configured, or a connection built on it.
        var setups = await Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            samlSignIn: ConfiguredSignIn()).ReadAsync();

        setups.Single(p => p.Key == "aws").InUse.Should().BeTrue();
    }

    [Fact]
    public async Task Azure_WithSignInConfiguredButNoConnection_IsInUse()
    {
        var azure = await AzureAsync(Build(Authenticated(AwsCredentialKind.StoredKey), Buckets("one"),
            azureAd: ConfiguredAzureAd()));

        azure.InUse.Should().BeTrue();
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
