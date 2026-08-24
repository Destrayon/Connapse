using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Connapse.Core.Utilities;

/// <summary>
/// A generated SSH key pair. The private half is stored; the public half is what the
/// operator installs on their own machine.
/// </summary>
/// <param name="PrivateKeyPem">
/// PKCS#1 PEM — <c>-----BEGIN RSA PRIVATE KEY-----</c>. The form SSH.NET reads.
/// </param>
/// <param name="PublicKeyLine">
/// A single <c>authorized_keys</c> line, ready to append.
/// </param>
public record SshKeyPair(string PrivateKeyPem, string PublicKeyLine);

/// <summary>
/// Generates an SSH key pair so setting up an SFTP connection never requires
/// <c>ssh-keygen</c>.
/// <para>
/// Generating server-side is not only the easier path, it is the better one. A pasted key
/// is usually a key the operator already has and uses elsewhere; a generated one exists for
/// a single connection, has never been on a clipboard, and is revoked by deleting that
/// connection. The private half is never rendered — it goes straight into the encrypted
/// column.
/// </para>
/// </summary>
public static class SshKeyPairGenerator
{
    /// <summary>
    /// RSA rather than Ed25519, which would otherwise be the obvious default: .NET exposes
    /// no Ed25519 primitive, and SSH.NET's key generation surface does not cover it either.
    /// 3072 bits is the NIST-equivalent of AES-128 and what <c>ssh-keygen</c> defaults to
    /// for RSA.
    /// </summary>
    public const int KeySizeBits = 3072;

    /// <summary>
    /// Generates a pair. <paramref name="comment"/> is the trailing label on the public key
    /// line, which is how an operator later recognises the entry in <c>authorized_keys</c>.
    /// </summary>
    public static SshKeyPair Generate(string comment = "connapse")
    {
        using var rsa = RSA.Create(KeySizeBits);

        return new SshKeyPair(
            rsa.ExportRSAPrivateKeyPem(),
            FormatPublicKey(rsa, comment));
    }

    /// <summary>
    /// Encodes an RSA public key the way <c>authorized_keys</c> expects: the algorithm name,
    /// the exponent, and the modulus, each length-prefixed, then base64.
    /// </summary>
    /// <remarks>
    /// A mistake here fails as "authentication failed" with nothing pointing at the cause,
    /// which is why the round trip against a real server is the test that matters.
    /// </remarks>
    public static string FormatPublicKey(RSA rsa, string comment = "connapse")
    {
        RSAParameters p = rsa.ExportParameters(includePrivateParameters: false);

        using var buffer = new MemoryStream();
        WriteLengthPrefixed(buffer, "ssh-rsa"u8.ToArray());
        WriteLengthPrefixed(buffer, ToMpint(p.Exponent!));
        WriteLengthPrefixed(buffer, ToMpint(p.Modulus!));

        string label = Sanitise(comment);

        return $"ssh-rsa {Convert.ToBase64String(buffer.ToArray())} {label}";
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(data);
    }

    /// <summary>
    /// SSH multiple-precision integers are signed, so a value whose top bit is set needs a
    /// leading zero byte or it reads as negative.
    /// </summary>
    private static byte[] ToMpint(byte[] value) =>
        value.Length > 0 && value[0] >= 0x80 ? [0, .. value] : value;

    /// <summary>
    /// The comment is the last field on the line, so anything that could end the line early
    /// or start a second entry has to go. An operator-supplied connection name reaches here.
    /// </summary>
    private static string Sanitise(string comment)
    {
        string trimmed = new string(comment
            .Where(c => !char.IsControl(c) && c != '\n' && c != '\r')
            .ToArray()).Trim();

        return string.IsNullOrEmpty(trimmed) ? "connapse" : trimmed;
    }
}
