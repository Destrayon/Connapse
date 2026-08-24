using System.Security.Cryptography;
using System.Text;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class SshHostKeyPolicyTests
{
    private const string Presented = "SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG";

    [Fact]
    public void Evaluate_NothingPinned_TrustsOnFirstUse()
    {
        SshHostKeyPolicy.Evaluate(null, Presented)
            .Should().Be(SshHostKeyDecision.TrustOnFirstUse);
    }

    /// <summary>
    /// Clearing the recorded fingerprint is the documented way to accept a legitimate rekey,
    /// so a blank value must re-arm trust on first use rather than read as a mismatch.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_ClearedFingerprint_ReArmsTrustOnFirstUse(string pinned)
    {
        SshHostKeyPolicy.Evaluate(pinned, Presented)
            .Should().Be(SshHostKeyDecision.TrustOnFirstUse);
    }

    [Fact]
    public void Evaluate_SamePinnedKey_Matches()
    {
        SshHostKeyPolicy.Evaluate(Presented, Presented)
            .Should().Be(SshHostKeyDecision.Matches);
    }

    [Fact]
    public void Evaluate_DifferentKey_IsAMismatch()
    {
        SshHostKeyPolicy.Evaluate("SHA256:somethingelse", Presented)
            .Should().Be(SshHostKeyDecision.Mismatch);
    }

    /// <summary>
    /// Base64 is case-significant, so two fingerprints differing only in case are two
    /// different keys. Normalising case here would let a mismatch through.
    /// </summary>
    [Fact]
    public void Evaluate_SameKeyInDifferentCase_IsAMismatch()
    {
        SshHostKeyPolicy.Evaluate(Presented.ToUpperInvariant(), Presented)
            .Should().Be(SshHostKeyDecision.Mismatch);
    }

    [Fact]
    public void Evaluate_StoredWithSurroundingWhitespace_StillMatches()
    {
        SshHostKeyPolicy.Evaluate($"  {Presented}\n", Presented)
            .Should().Be(SshHostKeyDecision.Matches);
    }

    /// <summary>
    /// The format has to survive a copy-paste comparison against
    /// <c>ssh-keyscan host | ssh-keygen -lf -</c>, which prints unpadded base64.
    /// </summary>
    [Fact]
    public void FormatFingerprint_MatchesTheOpenSshShape()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("a host key blob"));

        string formatted = SshHostKeyPolicy.FormatFingerprint(hash);

        formatted.Should().StartWith("SHA256:");
        formatted.Should().NotEndWith("=");
        formatted.Should().Be("SHA256:" + Convert.ToBase64String(hash).TrimEnd('='));
    }

    [Fact]
    public void FormatFingerprint_RoundTripsThroughEvaluate()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("stable"));

        string first = SshHostKeyPolicy.FormatFingerprint(hash);
        string second = SshHostKeyPolicy.FormatFingerprint(hash);

        SshHostKeyPolicy.Evaluate(first, second).Should().Be(SshHostKeyDecision.Matches);
    }

    [Fact]
    public void FormatFingerprint_DifferentKeys_ProduceDifferentFingerprints()
    {
        string a = SshHostKeyPolicy.FormatFingerprint(SHA256.HashData("one"u8));
        string b = SshHostKeyPolicy.FormatFingerprint(SHA256.HashData("two"u8));

        SshHostKeyPolicy.Evaluate(a, b).Should().Be(SshHostKeyDecision.Mismatch);
    }

    [Fact]
    public void FormatFingerprint_EmptyHash_Throws()
    {
        Action act = () => SshHostKeyPolicy.FormatFingerprint(ReadOnlySpan<byte>.Empty);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The operator's next step is deciding whether this was their own rekey, so both
    /// fingerprints have to be in the message.
    /// </summary>
    [Fact]
    public void DescribeMismatch_NamesBothFingerprintsAndTheHost()
    {
        string message = SshHostKeyPolicy.DescribeMismatch("files.example.com", "SHA256:old", "SHA256:new");

        message.Should().Contain("files.example.com");
        message.Should().Contain("SHA256:old");
        message.Should().Contain("SHA256:new");
    }
}
