using System.Security.Cryptography;
using System.Text;

namespace Connapse.Core.Utilities;

/// <summary>
/// Helpers for computing lowercase hex-encoded cryptographic hashes.
/// </summary>
public static class HexHash
{
    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="input"/> as a lowercase hex string (64 chars).</summary>
    public static string Sha256(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
