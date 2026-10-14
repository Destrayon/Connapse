using System.Buffers.Text;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>
/// A listing page token carries the last document ID returned and the change cursor taken before the first
/// page, so the cursor the last page returns covers every change made while the listing was read. The next
/// page starts after that ID rather than at an offset, so a document deleted mid-listing cannot shift the
/// rest past the page boundary.
/// </summary>
public static class ListingToken
{
    public static string Format(string lastId, string cursor) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{lastId}|{cursor}"));

    public static bool TryParse(string token, out string lastId, out string cursor)
    {
        lastId = "";
        cursor = "";
        if (!Base64Url.IsValid(token)) return false;

        string text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        int bar = text.IndexOf('|');
        if (bar <= 0) return false;

        lastId = text[..bar];
        cursor = text[(bar + 1)..];
        return cursor.Length > 0;
    }
}
