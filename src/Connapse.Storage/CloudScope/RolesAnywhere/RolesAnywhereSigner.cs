using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

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

    /// <summary>
    /// The SigV4 canonical request. <paramref name="sortedHeaders"/> must already be lowercase-named and
    /// ordinal-sorted by name; values are SigV4-normalised here (trimmed, internal space runs collapsed).
    /// </summary>
    public static string BuildCanonicalRequest(
        string httpMethod,
        string canonicalUri,
        string canonicalQueryString,
        IReadOnlyList<KeyValuePair<string, string>> sortedHeaders,
        string signedHeaders,
        string payloadHashHex)
    {
        var builder = new StringBuilder();
        builder.Append(httpMethod).Append('\n');
        builder.Append(canonicalUri).Append('\n');
        builder.Append(canonicalQueryString).Append('\n');
        foreach (KeyValuePair<string, string> header in sortedHeaders)
        {
            builder.Append(header.Key).Append(':').Append(TrimAll(header.Value)).Append('\n');
        }
        builder.Append('\n');
        builder.Append(signedHeaders).Append('\n');
        builder.Append(payloadHashHex);
        return builder.ToString();
    }

    /// <summary>
    /// SigV4 header-value normalisation ("Trimall"): trims leading/trailing spaces and collapses runs of
    /// internal spaces to a single space. AWS re-canonicalises header values this way, so a value with
    /// repeated spaces that was only <c>Trim()</c>ed would be signed differently from how AWS recomputes
    /// it and the request would be rejected.
    /// </summary>
    private static string TrimAll(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The SigV4 string-to-sign: algorithm, timestamp, credential scope, hashed canonical request.</summary>
    public static string BuildStringToSign(
        string algorithm, string amzDate, string credentialScope, string canonicalRequestHashHex)
        => $"{algorithm}\n{amzDate}\n{credentialScope}\n{canonicalRequestHashHex}";

    /// <summary>A signed CreateSession request: where to send it, the exact body bytes, and the headers.</summary>
    public sealed record SignedSessionRequest(
        string Url, string JsonBody, IReadOnlyList<KeyValuePair<string, string>> Headers);

    /// <summary>
    /// Builds and signs the CreateSession request. Deterministic given <paramref name="signingTime"/>,
    /// which is what makes the whole engine unit-testable without live AWS.
    /// </summary>
    public static SignedSessionRequest Sign(
        X509Certificate2 certificate, RolesAnywhereParameters parameters, DateTimeOffset signingTime)
    {
        DateTime utc = signingTime.UtcDateTime;
        string amzDate = utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string dateStamp = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string host = $"rolesanywhere.{parameters.Region}.amazonaws.com";

        string body = BuildBody(parameters);
        string payloadHash = Sha256Hex(Encoding.UTF8.GetBytes(body));
        string x509 = Convert.ToBase64String(certificate.RawData);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
            new("host", host),
            new("x-amz-date", amzDate),
            new("x-amz-x509", x509),
        };
        headers.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        const string signedHeaders = "content-type;host;x-amz-date;x-amz-x509";

        string canonicalRequest = BuildCanonicalRequest("POST", "/sessions", "", headers, signedHeaders, payloadHash);
        string credentialScope = $"{dateStamp}/{parameters.Region}/rolesanywhere/aws4_request";
        string algorithm = SelectAlgorithm(certificate);
        string stringToSign = BuildStringToSign(
            algorithm, amzDate, credentialScope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        string signatureHex = Convert.ToHexStringLower(SignBytes(certificate, algorithm, Encoding.UTF8.GetBytes(stringToSign)));
        string credential = $"{SerialDecimal(certificate)}/{credentialScope}";
        string authorization =
            $"{algorithm} Credential={credential}, SignedHeaders={signedHeaders}, Signature={signatureHex}";

        var outgoing = new List<KeyValuePair<string, string>>
        {
            new("content-type", "application/json"),
            new("host", host),
            new("x-amz-date", amzDate),
            new("x-amz-x509", x509),
            new("authorization", authorization),
        };

        return new SignedSessionRequest($"https://{host}/sessions", body, outgoing);
    }

    private static string BuildBody(RolesAnywhereParameters parameters)
    {
        var payload = new Dictionary<string, object>
        {
            ["profileArn"] = parameters.ProfileArn,
            ["roleArn"] = parameters.RoleArn,
            ["trustAnchorArn"] = parameters.TrustAnchorArn,
        };
        if (parameters.DurationSeconds is int seconds)
        {
            payload["durationSeconds"] = seconds;
        }
        if (!string.IsNullOrWhiteSpace(parameters.RoleSessionName))
        {
            payload["roleSessionName"] = parameters.RoleSessionName;
        }
        return JsonSerializer.Serialize(payload);
    }
}
