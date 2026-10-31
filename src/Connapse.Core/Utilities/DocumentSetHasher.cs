using System.Security.Cryptography;
using System.Text;

namespace Connapse.Core.Utilities;

/// <summary>
/// Hashes a set of documents one at a time, so a container of any size is hashed without holding its
/// documents in memory. Fed in the same order, it gives the same hash as
/// <c>HexHash.Sha256</c> of the lines <c>{id}|{contentHash}</c> joined with newlines.
/// </summary>
public sealed class DocumentSetHasher : IDisposable
{
    private static readonly byte[] Newline = "\n"u8.ToArray();

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public int Count { get; private set; }

    public void Add(string documentId, string? contentHash)
    {
        if (Count > 0) _hash.AppendData(Newline);
        _hash.AppendData(Encoding.UTF8.GetBytes($"{documentId}|{contentHash}"));
        Count++;
    }

    public DocumentSetFingerprint Finish() =>
        new(Count, Convert.ToHexStringLower(_hash.GetHashAndReset()));

    public void Dispose() => _hash.Dispose();
}
