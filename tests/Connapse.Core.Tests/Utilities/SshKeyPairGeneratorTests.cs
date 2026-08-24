using System.Security.Cryptography;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Utilities;

/// <summary>
/// These cover shape and safety. The encoding itself is only genuinely proven by
/// <c>SftpConnectorIntegrationTests.GeneratedKeyPair_AuthenticatesAgainstARealServer</c>,
/// because a wrong encoding produces a well-formed line that simply fails to authenticate.
/// </summary>
[Trait("Category", "Unit")]
public class SshKeyPairGeneratorTests
{
    [Fact]
    public void Generate_ProducesAPrivateKeyInTheFormSshNetReads()
    {
        var pair = SshKeyPairGenerator.Generate();

        pair.PrivateKeyPem.Should().StartWith("-----BEGIN RSA PRIVATE KEY-----");
        pair.PrivateKeyPem.Should().Contain("-----END RSA PRIVATE KEY-----");
    }

    [Fact]
    public void Generate_ProducesASingleAuthorizedKeysLine()
    {
        var pair = SshKeyPairGenerator.Generate();

        pair.PublicKeyLine.Should().StartWith("ssh-rsa ");
        pair.PublicKeyLine.Should().NotContain("\n");
        pair.PublicKeyLine.Split(' ').Should().HaveCount(3, "algorithm, key, comment");
    }

    [Fact]
    public void Generate_UsesTheRequestedKeySize()
    {
        var pair = SshKeyPairGenerator.Generate();

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pair.PrivateKeyPem);

        rsa.KeySize.Should().Be(SshKeyPairGenerator.KeySizeBits);
    }

    [Fact]
    public void Generate_PublicHalfMatchesThePrivateHalf()
    {
        var pair = SshKeyPairGenerator.Generate();

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pair.PrivateKeyPem);

        SshKeyPairGenerator.FormatPublicKey(rsa).Should().Be(pair.PublicKeyLine);
    }

    [Fact]
    public void Generate_EachCallProducesADifferentKey()
    {
        SshKeyPairGenerator.Generate().PublicKeyLine
            .Should().NotBe(SshKeyPairGenerator.Generate().PublicKeyLine);
    }

    [Fact]
    public void Generate_CommentAppearsAsTheLabel()
    {
        SshKeyPairGenerator.Generate("my-laptop").PublicKeyLine
            .Should().EndWith(" my-laptop");
    }

    /// <summary>
    /// The comment is the final field on the line, so a newline in it would end the entry
    /// and let whatever followed be parsed as a second authorized key. Connection names reach
    /// here, and they are operator-supplied.
    /// </summary>
    [Fact]
    public void Generate_CommentCannotInjectASecondAuthorizedKey()
    {
        var pair = SshKeyPairGenerator.Generate("ok\nssh-rsa AAAAB3NzaC1yc2EAAAADAQAB attacker");

        pair.PublicKeyLine.Should().NotContain("\n");
        pair.PublicKeyLine.Split('\n').Should().HaveCount(1);
        pair.PublicKeyLine.Should().NotContain("attacker\n");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\r")]
    public void Generate_BlankComment_FallsBackToADefaultLabel(string comment)
    {
        SshKeyPairGenerator.Generate(comment).PublicKeyLine
            .Should().EndWith(" connapse");
    }

    /// <summary>
    /// An mpint is signed, so a modulus whose top bit is set needs a leading zero byte. A
    /// 3072-bit RSA modulus always has its top bit set, so this path runs every time — and if
    /// it were wrong, every generated key would fail to authenticate.
    /// </summary>
    [Fact]
    public void FormatPublicKey_ModulusIsEncodedAsAPositiveMpint()
    {
        var pair = SshKeyPairGenerator.Generate();
        byte[] blob = Convert.FromBase64String(pair.PublicKeyLine.Split(' ')[1]);

        var reader = new BlobReader(blob);

        System.Text.Encoding.ASCII.GetString(reader.Next()).Should().Be("ssh-rsa");
        reader.Next().Should().NotBeEmpty("the exponent must be present");

        byte[] modulus = reader.Next();
        modulus[0].Should().Be(0, "a 3072-bit modulus has its top bit set and needs the sign byte");
        (modulus.Length - 1).Should().Be(SshKeyPairGenerator.KeySizeBits / 8);
    }

    /// <summary>Walks the length-prefixed fields of an SSH key blob.</summary>
    private sealed class BlobReader(byte[] blob)
    {
        private int _offset;

        public byte[] Next()
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(
                blob.AsSpan(_offset, 4));
            _offset += 4;

            byte[] value = blob[_offset..(_offset + length)];
            _offset += length;
            return value;
        }
    }
}
