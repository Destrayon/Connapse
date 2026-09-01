using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereSignerTests
{
    [Fact]
    public void Sha256Hex_EmptyInput_MatchesAwsKnownConstant()
    {
        string hash = RolesAnywhereSigner.Sha256Hex(Array.Empty<byte>());

        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void SerialDecimal_IsDecimalRepresentationOfCertSerial_NotHex()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();

        string decimalSerial = RolesAnywhereSigner.SerialDecimal(cert);

        var expected = System.Numerics.BigInteger.Parse(
            "00" + cert.SerialNumber, System.Globalization.NumberStyles.HexNumber);
        decimalSerial.Should().Be(expected.ToString(System.Globalization.CultureInfo.InvariantCulture));
        decimalSerial.Should().MatchRegex("^[0-9]+$");
    }
}
