using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Reads the certificate files Connapse's Azure identities sign with, in the two shapes the settings
/// accept: a PEM holding certificate plus private key (<c>.pem</c>/<c>.crt</c>), or a PKCS#12 bundle
/// with an optional password. One loader, so the credential chain, the expiry shown on the provider
/// page, and the setup check all read the same file the same way.
/// </summary>
public static class AzureCertificateFile
{
    /// <summary>The certificate at <paramref name="path"/>, or null when no path is set or the file is missing.</summary>
    /// <exception cref="CryptographicException">The file exists but is not a readable certificate (or the password is wrong).</exception>
    public static X509Certificate2? Load(string? path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(path)
            : X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }

    /// <summary>When the certificate at <paramref name="path"/> stops being accepted, in UTC; null
    /// when there is no readable certificate there.</summary>
    public static DateTime? Expiry(string? path, string? password)
    {
        try
        {
            using X509Certificate2? certificate = Load(path, password);
            return certificate?.NotAfter.ToUniversalTime();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The SHA-1 thumbprint of a PEM-encoded certificate, upper-case hex with no separators —
    /// the form <c>openssl x509 -fingerprint</c> prints once its colons are stripped, and what Entra
    /// shows under Certificates &amp; secrets.</summary>
    public static string Thumbprint(string certificatePem)
    {
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(certificatePem);
        return certificate.Thumbprint.ToUpperInvariant();
    }
}
