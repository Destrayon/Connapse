using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// A single linear pass over raw HTML, for pages too deeply nested to give to a DOM parser.
/// <para>
/// AngleSharp's tree builder checks the stack of open elements on most start tags, so its cost
/// grows with the square of the nesting depth: 100,000 nested <c>div</c>s, about a megabyte, took
/// 45 seconds to parse. Readability and the Markdown converter then recurse once per level, and a
/// stack overflow ends the worker process. This pass measures the depth first, and reads a page
/// that is too deep as plain text without building a DOM at all.
/// </para>
/// </summary>
internal static class HtmlMarkup
{
    /// <summary>Elements with no content and no end tag.</summary>
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    /// <summary>
    /// Elements whose end tag is commonly left out and which a parser closes when the next one
    /// starts, so they never nest by themselves. Counting them would call ordinary hand-written
    /// HTML deep.
    /// </summary>
    private static readonly HashSet<string> ImplicitlyClosedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "li", "dt", "dd", "tr", "td", "th", "option", "optgroup", "thead", "tbody", "tfoot", "colgroup",
        "caption", "rb", "rt", "rp", "rtc", "html", "head", "body",
    };

    /// <summary>
    /// Elements whose content is not markup, read up to their end tag. Not noscript or template:
    /// with scripting off, AngleSharp parses what is inside them as elements, so their content
    /// nests like any other.
    /// </summary>
    private static readonly HashSet<string> RawTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes", "plaintext",
    };

    /// <summary>SVG and MathML, inside which "/>" really does close an element.</summary>
    private static readonly HashSet<string> ForeignRootTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "svg", "math",
    };

    /// <summary>Elements that start a new line when the page is read as plain text.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6", "table", "ul", "ol", "pre",
        "blockquote", "section", "article", "header", "footer", "dl", "dt", "dd", "figcaption", "hr",
        "main", "nav", "aside", "td", "th",
    };

    /// <summary>
    /// True when elements nest deeper than <paramref name="limit"/>. End tags close the innermost
    /// open element of the same name, as a parser would; a stray end tag closes nothing, so it
    /// cannot hide depth from the count.
    /// </summary>
    public static bool NestsDeeperThan(string html, int limit)
    {
        var open = new List<string>();
        foreach (var token in Tokens(html))
        {
            if (token.Name is null || VoidTags.Contains(token.Name) || ImplicitlyClosedTags.Contains(token.Name))
                continue;

            if (token.IsEnd)
            {
                int index = open.FindLastIndex(n => n.Equals(token.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    open.RemoveRange(index, open.Count - index);
            }
            else if (!RawTextTags.Contains(token.Name)
                && !(token.SelfClosing && (ForeignRootTags.Contains(token.Name) || open.Exists(ForeignRootTags.Contains))))
            {
                // An HTML element's "/>" is ignored: "<div/>" opens a div like "<div>" does.
                open.Add(token.Name);
                if (open.Count > limit)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The value of the first <c>charset</c> a real meta tag declares, or null. Comments and script
    /// text are skipped, so a commented-out declaration cannot outrank the page's own.
    /// </summary>
    public static string? DeclaredCharset(string head)
    {
        foreach (var token in Tokens(head))
        {
            if (token.Name is not null && !token.IsEnd && token.Name.Equals("meta", StringComparison.OrdinalIgnoreCase)
                && MetaCharset.Match(head, token.Start, token.Length) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static readonly Regex MetaCharset = new(
        @"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The page's text, one paragraph per block element, without building a DOM.</summary>
    public static string PlainText(string html)
    {
        var text = new StringBuilder();
        foreach (var token in Tokens(html))
        {
            if (token.Name is null)
                text.Append(html, token.Start, token.Length);
            else if (BlockTags.Contains(token.Name))
                text.Append('\n');
        }

        var lines = WebUtility.HtmlDecode(text.ToString()).Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .Select(MarkdownText.EscapeLine);
        return string.Join("\n\n", lines);
    }

    /// <summary>A run of text (Name null) or a tag. Comments, doctypes and raw-text content are skipped.</summary>
    private readonly record struct Token(int Start, int Length, string? Name, bool IsEnd, bool SelfClosing);

    private static IEnumerable<Token> Tokens(string html)
    {
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                yield return new Token(i, html.Length - i, null, false, false);
                yield break;
            }

            if (lt > i)
                yield return new Token(i, lt - i, null, false, false);

            if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0)
            {
                int close = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = close < 0 ? html.Length : close + 3;
                continue;
            }

            bool isEnd = lt + 1 < html.Length && html[lt + 1] == '/';
            int nameStart = lt + (isEnd ? 2 : 1);
            int nameEnd = nameStart;
            while (nameEnd < html.Length && (char.IsAsciiLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-'))
                nameEnd++;

            // "<" not followed by a tag name is text, as are doctypes and processing instructions'
            // leading characters once skipped.
            if (nameEnd == nameStart || !char.IsAsciiLetter(html[nameStart]))
            {
                if (lt + 1 < html.Length && html[lt + 1] is '!' or '?')
                {
                    int close = html.IndexOf('>', lt);
                    i = close < 0 ? html.Length : close + 1;
                }
                else
                {
                    yield return new Token(lt, 1, null, false, false);
                    i = lt + 1;
                }
                continue;
            }

            int gt = html.IndexOf('>', nameEnd);
            if (gt < 0)
                yield break;

            string name = html[nameStart..nameEnd];
            bool selfClosing = !isEnd && html[gt - 1] == '/';
            yield return new Token(lt, gt + 1 - lt, name, isEnd, selfClosing);
            i = gt + 1;

            if (!isEnd && !selfClosing && RawTextTags.Contains(name))
            {
                int close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                i = close < 0 ? html.Length : close;
            }
        }
    }
}
