using Amazon.Runtime;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

/// <summary>
/// Naming the chain link a credential came from.
/// </summary>
/// <remarks>
/// The AWS calls themselves are not tested here — they need AWS. What is tested is the one piece of
/// judgement in the class: the default provider chain resolves a static key from years ago exactly
/// as smoothly as an instance role and reports no difference, and this mapping is the only thing
/// standing between that and an operator never being told.
/// </remarks>
[Trait("Category", "Unit")]
public class S3DiscoveryTests
{
    /// <remarks>
    /// By type rather than by instance. Several of these classes do real work in their
    /// constructors: <c>EnvironmentVariablesAWSCredentials</c> throws when the variables are
    /// unset, and <c>InstanceProfileAWSCredentials</c> calls the EC2 metadata service and takes
    /// the better part of a second to fail off-EC2. Constructing them here made the suite depend
    /// on the machine it ran on.
    /// </remarks>
    [Theory]
    // The most common way a container ends up with credentials, and the weakest: it never expires.
    [InlineData(typeof(EnvironmentVariablesAWSCredentials), AwsCredentialKind.StaticKey)]
    [InlineData(typeof(BasicAWSCredentials), AwsCredentialKind.StaticKey)]
    // Temporary by construction -- they carry a session token and expire.
    [InlineData(typeof(SessionAWSCredentials), AwsCredentialKind.AssumedRole)]
    [InlineData(typeof(AssumeRoleAWSCredentials), AwsCredentialKind.AssumedRole)]
    // The one posture needing no arrangement at all, and it rotates itself.
    [InlineData(typeof(InstanceProfileAWSCredentials), AwsCredentialKind.InstanceOrTaskRole)]
    [InlineData(typeof(GenericContainerCredentials), AwsCredentialKind.InstanceOrTaskRole)]
    // Expires, and needs a browser sign-in to renew, so it cannot sustain unattended sync.
    [InlineData(typeof(SSOAWSCredentials), AwsCredentialKind.SsoSession)]
    // How IAM Roles Anywhere arrives: temporary credentials, no static keys.
    [InlineData(typeof(ProcessAWSCredentials), AwsCredentialKind.ExternalProcess)]
    public void ClassifyType_NamesTheChainLink(Type type, AwsCredentialKind expected)
    {
        S3Discovery.ClassifyType(type).Should().Be(expected);
    }

    [Fact]
    public void ClassifyType_ConnapseOwnCredential_IsAStoredKey()
    {
        // The regression this pins: ConnapseAwsCredentials derives from RefreshingAWSCredentials,
        // which matches nothing in the chain, so the identity Connapse creates for itself was
        // reported as "source not recognised" -- and Unrecognised was not treated as weak, so the
        // page put a green tick beside it.
        S3Discovery.ClassifyType(typeof(ConnapseAwsCredentials))
            .Should().Be(AwsCredentialKind.StoredKey);
    }

    [Fact]
    public void ClassifyType_StoredKey_IsNotConfusedWithAnAmbientOne()
    {
        // Deliberately distinct. An ambient key is somebody's, of unknown age and scope; this one
        // is read-only, Connapse's alone, and revocable -- so the UI says different things about
        // them, and collapsing the two would make the recommended setup warn about itself.
        S3Discovery.ClassifyType(typeof(ConnapseAwsCredentials))
            .Should().NotBe(AwsCredentialKind.StaticKey);
    }

    [Fact]
    public void ClassifyType_UnknownType_IsUnrecognisedRatherThanStrong()
    {
        // A newer SDK adding a chain link must not be reported as an instance role by default.
        // Unrecognised is honest; anything else is a claim the code cannot support.
        S3Discovery.ClassifyType(typeof(AnonymousAWSCredentials))
            .Should().Be(AwsCredentialKind.Unrecognised);
    }

    [Fact]
    public void ClassifyType_Null_IsUnrecognised()
    {
        S3Discovery.ClassifyType(null).Should().Be(AwsCredentialKind.Unrecognised);
    }

    [Fact]
    public void ClassifyType_DerivedCredential_ClassifiesAsWhatItDerivesFrom()
    {
        // The SDK wraps and subclasses these internally, and a wrapper reported as Unrecognised
        // would quietly stop warning about a static key.
        S3Discovery.ClassifyType(typeof(DerivedBasicCredentials))
            .Should().Be(AwsCredentialKind.StaticKey);
    }

    private sealed class DerivedBasicCredentials(string a, string b) : BasicAWSCredentials(a, b);

    /// <summary>
    /// The exception hierarchy the credential resolver's catch depends on.
    /// </summary>
    /// <remarks>
    /// Guards a bug that shipped and took the Blazor circuit down with "An unhandled error has
    /// occurred". The resolve was wrapped in <c>catch (AmazonServiceException)</c>, which reads as
    /// the careful, specific choice. The resolver throws <c>AmazonClientException</c> when nothing
    /// is configured — and these two are <b>siblings</b>, each deriving straight from
    /// <c>Exception</c> rather than one from the other. So catching either one catches nothing
    /// whatsoever of the other, and the most likely state of a fresh deployment escaped.
    /// <para>
    /// Asserted rather than commented, because the instinct when tidying is to replace a broad
    /// catch with a specific one, and here no single AWS exception type covers the case.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void TheTwoAwsExceptionRoots_AreSiblings_SoNeitherCatchCoversTheOther()
    {
        typeof(AmazonServiceException).IsSubclassOf(typeof(AmazonClientException))
            .Should().BeFalse("catching the service exception does not catch the client one");

        typeof(AmazonClientException).IsSubclassOf(typeof(AmazonServiceException))
            .Should().BeFalse("nor the other way round");

        typeof(AmazonClientException).BaseType.Should().Be<Exception>();
        typeof(AmazonServiceException).BaseType.Should().Be<Exception>();
    }

    /// <summary>
    /// The three outcomes the UI branches on.
    /// </summary>
    /// <remarks>
    /// Collapsing "no credentials" into "denied" is the specific failure the type exists to
    /// prevent: the first is fixed in a compose file and the second in AWS, and the advice for one
    /// is useless for the other.
    /// </remarks>
    public class ProbeTests
    {
        [Fact]
        [Trait("Category", "Unit")]
        public void NoCredentials_AndDenied_AreNotTheSameOutcome()
        {
            var missing = AwsProbe<string>.NoCredentials();
            var denied = AwsProbe<string>.Denied();

            missing.Outcome.Should().NotBe(denied.Outcome);
            missing.Succeeded.Should().BeFalse();
            denied.Succeeded.Should().BeFalse();
        }

        [Fact]
        [Trait("Category", "Unit")]
        public void Ok_CarriesTheValueAndSucceeds()
        {
            var probe = AwsProbe<string>.Ok("us-west-1");

            probe.Succeeded.Should().BeTrue();
            probe.Value.Should().Be("us-west-1");
        }
    }
}
