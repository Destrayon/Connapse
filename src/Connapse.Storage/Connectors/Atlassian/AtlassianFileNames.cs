using System.Text;
using System.Text.RegularExpressions;

namespace Connapse.Storage.Connectors.Atlassian;

/// <summary>File names of Confluence and Jira attachments, made safe to use in a document path.</summary>
internal static partial class AtlassianFileNames
{
    /// <summary>
    /// A file name safe to use as the last path segment: no separators, control characters or
    /// characters Windows refuses, no run of dots, no leading or trailing dots or spaces, at most
    /// 200 characters with the extension kept. The same name always comes out the same.
    /// </summary>
    internal static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

        string safe = DotRun().Replace(sb.ToString(), ".").Trim(' ', '.');
        if (safe.Length > 200)
        {
            string extension = Prefix(Path.GetExtension(safe), 20);
            safe = Prefix(safe, 200 - extension.Length) + extension;
        }

        return safe.Length == 0 ? "attachment" : safe;
    }

    /// <summary>The first <paramref name="length"/> UTF-16 units, one fewer if that would split a surrogate pair.</summary>
    private static string Prefix(string value, int length)
    {
        if (value.Length <= length)
            return value;
        return char.IsHighSurrogate(value[length - 1]) ? value[..(length - 1)] : value[..length];
    }

    [GeneratedRegex(@"\.{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DotRun();
}
