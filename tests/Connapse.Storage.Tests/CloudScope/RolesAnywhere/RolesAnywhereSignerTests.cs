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

    [Fact]
    public void SerialDecimal_KnownFixedSerial_ReturnsExactDecimalString()
    {
        using RSA rsa = RSA.Create(2048);
        var name = new X500DistinguishedName("CN=connapse-serial-test");
        var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
        byte[] serialNumber = { 0x01, 0x23 }; // big-endian 0x0123 == 291 decimal
        var request = new CertificateRequest(name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.Create(
            name, generator, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), serialNumber);

        RolesAnywhereSigner.SerialDecimal(cert).Should().Be("291");
    }

    [Fact]
    public void SelectAlgorithm_RsaCertificate_ReturnsRsaAlgorithm()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
        RolesAnywhereSigner.SelectAlgorithm(cert).Should().Be("AWS4-X509-RSA-SHA256");
    }

    [Fact]
    public void SelectAlgorithm_EcCertificate_ReturnsEcdsaAlgorithm()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateEc();
        RolesAnywhereSigner.SelectAlgorithm(cert).Should().Be("AWS4-X509-ECDSA-SHA256");
    }

    [Fact]
    public void SignBytes_RsaSignature_VerifiesAgainstCertificatePublicKey()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
        byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

        byte[] signature = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);

        using RSA pub = cert.GetRSAPublicKey()!;
        pub.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
           .Should().BeTrue();
    }

    [Fact]
    public void SignBytes_RsaSignature_IsDeterministic()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateRsa();
        byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

        byte[] first = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);
        byte[] second = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-RSA-SHA256", data);

        first.Should().Equal(second); // PKCS#1 v1.5 is deterministic
    }

    [Fact]
    public void SignBytes_EcdsaSignature_VerifiesAsDerSequence()
    {
        using X509Certificate2 cert = CertificateTestFactory.CreateEc();
        byte[] data = Encoding.UTF8.GetBytes("string-to-sign");

        byte[] signature = RolesAnywhereSigner.SignBytes(cert, "AWS4-X509-ECDSA-SHA256", data);

        using ECDsa pub = cert.GetECDsaPublicKey()!;
        pub.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
           .Should().BeTrue();
    }

    [Fact]
    public void BuildCanonicalRequest_MatchesAwsExampleLayout_ForEmptyPayload()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
            new("host", "rolesanywhere.us-east-1.amazonaws.com"),
            new("x-amz-date", "20211103T120000Z"),
            new("x-amz-x509", "BASE64DER"),
        };
        string emptyPayloadHash = RolesAnywhereSigner.Sha256Hex(Array.Empty<byte>());

        string canonical = RolesAnywhereSigner.BuildCanonicalRequest(
            "POST", "/sessions", "", headers,
            "content-type;host;x-amz-date;x-amz-x509", emptyPayloadHash);

        string expected =
            "POST\n" +
            "/sessions\n" +
            "\n" +
            "content-type:application/json\n" +
            "host:rolesanywhere.us-east-1.amazonaws.com\n" +
            "x-amz-date:20211103T120000Z\n" +
            "x-amz-x509:BASE64DER\n" +
            "\n" +
            "content-type;host;x-amz-date;x-amz-x509\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        canonical.Should().Be(expected);
    }

    [Fact]
    public void BuildCanonicalRequest_TrimsHeaderValues()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", "  example  "),
        };

        string canonical = RolesAnywhereSigner.BuildCanonicalRequest(
            "POST", "/sessions", "", headers, "host", "HASH");

        canonical.Should().Contain("host:example\n");
    }

    [Fact]
    public void BuildStringToSign_HasFourLinesInFixedOrder()
    {
        string sts = RolesAnywhereSigner.BuildStringToSign(
            "AWS4-X509-RSA-SHA256",
            "20211101T121030Z",
            "20211101/us-east-1/rolesanywhere/aws4_request",
            "abc123");

        sts.Should().Be(
            "AWS4-X509-RSA-SHA256\n" +
            "20211101T121030Z\n" +
            "20211101/us-east-1/rolesanywhere/aws4_request\n" +
            "abc123");
    }
}
