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
}
