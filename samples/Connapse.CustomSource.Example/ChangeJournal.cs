using System.Security.Cryptography;

namespace Connapse.CustomSource.Example;

public sealed class ChangeJournal
{
    private readonly string _epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public string HeadCursor(string scopeId) => $"{_epoch}.0";
}
