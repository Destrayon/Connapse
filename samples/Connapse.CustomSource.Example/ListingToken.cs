using System.Buffers.Text;
using System.Text;

namespace Connapse.CustomSource.Example;

/// <summary>
/// A listing page token carries the offset of the next page and the change cursor taken before the first
/// page, so the cursor the last page returns covers every change made while the listing was read.
/// </summary>
public static class ListingToken
{
    public static string Format(int offset, string cursor) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{offset}|{cursor}"));

    public static bool TryParse(string token, out int offset, out string cursor)
    {
        offset = 0;
        cursor = "";
        if (!Base64Url.IsValid(token)) return false;

        string text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        int bar = text.IndexOf('|');
        if (bar <= 0 || !int.TryParse(text[..bar], out offset) || offset < 0) return false;

        cursor = text[(bar + 1)..];
        return cursor.Length > 0;
    }
}
