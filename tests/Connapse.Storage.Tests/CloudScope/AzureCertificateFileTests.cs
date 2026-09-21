using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

/// <summary>
/// The one loader every reader of Connapse's Azure certificate files goes through. A file it cannot
/// read is a fact for the provider page to show, never an exception that stops the page rendering.
/// </summary>
[Trait("Category", "Unit")]
public class AzureCertificateFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "connapse-cert-tests-" + Guid.NewGuid().ToString("N"));

    public AzureCertificateFileTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Expiry_MissingFile_IsNull()
    {
        AzureCertificateFile.Expiry(Path.Combine(directory, "nope.pem"), null).Should().BeNull();
        AzureCertificateFile.Expiry(null, null).Should().BeNull();
        AzureCertificateFile.Expiry("   ", null).Should().BeNull();
    }

    [Fact]
    public void Expiry_GeneratedPem_IsTheCertificatesNotAfter()
    {
        AzureCertificateMaterial material = AzureCertificateGenerator.Generate("connapse-test");
        string path = Write("good.pem", material.CombinedPem);

        DateTime? expiry = AzureCertificateFile.Expiry(path, null);

        using var expected = X509Certificate2.CreateFromPem(material.PublicCertificatePem);
        expiry.Should().Be(expected.NotAfter.ToUniversalTime());
    }

    [Fact]
    public void Expiry_FileThatIsNotACertificate_IsNull_NotAnException()
    {
        string path = Write("junk.pem", "this is not a certificate");

        AzureCertificateFile.Expiry(path, null).Should().BeNull();
    }

    [Fact]
    public void Expiry_FileHeldOpenExclusively_IsNull_NotAnException()
    {
        AzureCertificateMaterial material = AzureCertificateGenerator.Generate("connapse-test");
        string path = Write("locked.pem", material.CombinedPem);

        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        AzureCertificateFile.Expiry(path, null).Should().BeNull();
    }

    [Fact]
    public void Thumbprint_MatchesTheCertificatesOwn_UpperCaseNoSeparators()
    {
        AzureCertificateMaterial material = AzureCertificateGenerator.Generate("connapse-test");

        string thumbprint = AzureCertificateFile.Thumbprint(material.PublicCertificatePem);

        using var certificate = X509Certificate2.CreateFromPem(material.PublicCertificatePem);
        thumbprint.Should().Be(certificate.Thumbprint.ToUpperInvariant());
        thumbprint.Should().HaveLength(40).And.MatchRegex("^[0-9A-F]+$");
    }

    [Theory]
    [InlineData(typeof(CryptographicException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public void IsUnreadable_CoversTheWaysAFileFailsToRead(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;
        AzureCertificateFile.IsUnreadable(exception).Should().BeTrue();
    }

    [Fact]
    public void IsUnreadable_DoesNotSwallowEverything() =>
        AzureCertificateFile.IsUnreadable(new InvalidOperationException()).Should().BeFalse();
}
