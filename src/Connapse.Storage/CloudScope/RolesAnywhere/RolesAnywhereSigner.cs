using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope.RolesAnywhere;

/// <summary>
/// Signs an IAM Roles Anywhere <c>CreateSession</c> request with SigV4-X509: the same canonical
/// request and string-to-sign as ordinary SigV4, but the final signature is an asymmetric X.509
/// signature made with the certificate's private key, and the credential carries the certificate
/// serial instead of an access-key id.
/// </summary>
public static class RolesAnywhereSigner
{
    /// <summary>Lowercase hex of the SHA-256 of <paramref name="data"/>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// The certificate serial number as a decimal string. AWS puts this in the Credential field, and
    /// expects decimal — emitting the hex form (which <see cref="X509Certificate2.SerialNumber"/>
    /// returns) is a silent rejection.
    /// </summary>
    public static string SerialDecimal(X509Certificate2 certificate)
    {
        // Prepend "00" so the high bit never reads as a negative BigInteger.
        var serial = BigInteger.Parse("00" + certificate.SerialNumber, NumberStyles.HexNumber);
        return serial.ToString(CultureInfo.InvariantCulture);
    }

    public const string RsaAlgorithm = "AWS4-X509-RSA-SHA256";
    public const string EcdsaAlgorithm = "AWS4-X509-ECDSA-SHA256";

    /// <summary>
    /// The SigV4-X509 algorithm string for this certificate's key type. AWS rejects a request whose
    /// declared algorithm does not match the certificate's public key, so it is derived from the key,
    /// never guessed.
    /// </summary>
    public static string SelectAlgorithm(X509Certificate2 certificate)
    {
        using (RSA? rsa = certificate.GetRSAPrivateKey())
        {
            if (rsa is not null) return RsaAlgorithm;
        }
        using (ECDsa? ecdsa = certificate.GetECDsaPrivateKey())
        {
            if (ecdsa is not null) return EcdsaAlgorithm;
        }
        throw new InvalidOperationException("Certificate has neither an RSA nor an ECDSA private key.");
    }

    /// <summary>
    /// Signs <paramref name="data"/> (the string-to-sign bytes) with the certificate's private key.
    /// RSA is PKCS#1 v1.5 over SHA-256; ECDSA is SHA-256 with a DER-encoded signature — the two forms
    /// AWS accepts. The raw IEEE-P1363 ECDSA form the default overload produces is rejected.
    /// </summary>
    public static byte[] SignBytes(X509Certificate2 certificate, string algorithm, byte[] data)
    {
        if (algorithm == RsaAlgorithm)
        {
            using RSA rsa = certificate.GetRSAPrivateKey()
                ?? throw new InvalidOperationException("RSA algorithm selected but certificate has no RSA private key.");
            return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        if (algorithm == EcdsaAlgorithm)
        {
            using ECDsa ecdsa = certificate.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException("ECDSA algorithm selected but certificate has no ECDSA private key.");
            return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported Roles Anywhere algorithm.");
    }
}
