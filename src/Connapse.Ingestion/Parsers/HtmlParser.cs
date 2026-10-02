using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Connapse.Core.Interfaces;
using SmartReader;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for saved web pages (.html, .htm), written out as Markdown (#600).
/// <para>
/// The page's main content is found with SmartReader, a port of Mozilla Readability, so menus,
/// sidebars, cookie banners and footers are not indexed alongside the article. A page Readability
/// finds no article in -- a short note, a table of data, a page of links -- is converted whole.
/// Headings, lists and tables are kept as Markdown so the DocumentAware chunker can split on them.
/// </para>
/// <para>
/// Nothing is fetched. The page is parsed from the uploaded bytes and handed to SmartReader as a
/// document, which keeps it from loading the page's address; images are dropped (Connapse does not
/// index images) and links keep their text but not their targets.
/// </para>
/// </summary>
public class HtmlParser : IDocumentParser
{
    /// <summary>
    /// Deeper than this, the page is read as plain text (see <see cref="HtmlMarkup"/>). Readability
    /// and the Markdown converter both recurse once per level of nesting, and a stack overflow
    /// cannot be caught: it ends the worker process. Real pages nest a few dozen levels; only a
    /// crafted file comes near this.
    /// </summary>
    internal const int MaxConvertibleDepth = 400;

    /// <summary>The address SmartReader is told the page came from. It is never requested.</summary>
    private const string PageAddress = "https://document.invalid/";

    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html",
        ".htm",
    };

    /// <summary>Elements that are never readable text.</summary>
    private static readonly string[] NonContentSelectors =
    [
        "script", "style", "noscript", "template", "svg", "math", "canvas", "iframe", "frame", "object",
        "embed", "img", "picture", "video", "audio", "source", "track", "map", "input", "select",
        "textarea", "button", "link", "meta",
    ];

    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant);

    // A declared charset such as windows-1252 or Shift_JIS is looked up before TextDecoding,
    // which registers the same provider, has necessarily been touched.
    static HtmlParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public int Version => 1;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string> { ["FileType"] = "HTML" };

        try
        {
            var bytes = await TextParser.ReadAllBytesAsync(stream, cancellationToken);
            var (html, encoding) = Decode(bytes.Span);
            metadata["Encoding"] = encoding.WebName;

            // AngleSharp, SmartReader and ReverseMarkdown are synchronous.
            string content = await Task.Run(() => Convert(html, metadata, warnings, cancellationToken), cancellationToken);

            if (string.IsNullOrWhiteSpace(content))
            {
                warnings.Add("Document contains no readable text content");
                content = string.Empty;
            }

            return new ParsedDocument(content, metadata, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing HTML document: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    /// <summary>
    /// A byte order mark wins, then a charset the page declares in its first kilobyte -- the order
    /// browsers use -- and only then a guess from the bytes. A page saved as Windows-1252 that says
    /// so would otherwise be at the mercy of the detector.
    /// </summary>
    internal static (string Text, Encoding Encoding) Decode(ReadOnlySpan<byte> bytes)
    {
        bool hasBom = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
            || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]);

        if (!hasBom && DeclaredEncoding(bytes) is { } declared)
            return (declared.GetString(bytes), declared);

        return TextDecoding.Decode(bytes);
    }

    private static Encoding? DeclaredEncoding(ReadOnlySpan<byte> bytes)
    {
        string head = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 1024)]);
        if (HtmlMarkup.DeclaredCharset(head) is not { } charset)
            return null;

        try
        {
            var encoding = Encoding.GetEncoding(charset);

            // A UTF-16 declaration in bytes that were readable as ASCII is wrong by definition;
            // the HTML standard reads such a page as UTF-8.
            return encoding is UnicodeEncoding ? Encoding.UTF8 : encoding;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Convert(string html, Dictionary<string, string> metadata, List<string> warnings, CancellationToken ct)
    {
        // Measured on the markup before any DOM is built: parsing is itself quadratic in depth.
        if (HtmlMarkup.NestsDeeperThan(html, MaxConvertibleDepth))
            return AsPlainText(html, metadata, warnings);

        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var page = parser.ParseDocument(html);
        ct.ThrowIfCancellationRequested();

        // The parser can nest deeper than the markup reads, when it reopens formatting elements.
        if (MaxDepth(page.DocumentElement) > MaxConvertibleDepth)
            return AsPlainText(html, metadata, warnings);

        string title = Normalize(page.Title);
        if (title.Length > 0)
            metadata["Title"] = title;

        string? body = ExtractArticle(page, title, metadata, ct);
        if (body is null)
        {
            // Readability edits the page as it scores it, so the whole-page fallback starts again
            // from the original markup.
            var whole = parser.ParseDocument(html);
            RemoveNonContent(whole);
            body = whole.Body?.InnerHtml ?? string.Empty;
            metadata["ContentExtraction"] = "FullPage";
        }

        ct.ThrowIfCancellationRequested();
        string markdown = Tidy(ToMarkdown(body));

        // Readability takes the title heading out of the article, and a page converted whole may
        // have none; either way the title is what most searches for the page will name.
        if (title.Length > 0 && !HasTopLevelHeading(markdown))
            markdown = $"# {MarkdownText.EscapeLine(title)}\n\n{markdown}";

        return markdown;
    }

    private static string AsPlainText(string html, Dictionary<string, string> metadata, List<string> warnings)
    {
        warnings.Add($"The page nests elements more than {MaxConvertibleDepth} levels deep, so it was read as plain text without headings or tables.");
        metadata["ContentExtraction"] = "PlainText";
        return Tidy(HtmlMarkup.PlainText(html));
    }

    /// <summary>The article's HTML, cleaned, or null when Readability found no article.</summary>
    private static string? ExtractArticle(IHtmlDocument page, string title, Dictionary<string, string> metadata, CancellationToken ct)
    {
        var reader = new Reader(PageAddress, page);
        var article = reader.GetArticle();
        ct.ThrowIfCancellationRequested();

        if (!article.IsReadable || string.IsNullOrWhiteSpace(article.Content))
            return null;

        metadata["ContentExtraction"] = "Article";
        if (!string.IsNullOrWhiteSpace(article.Byline))
            metadata["Author"] = Normalize(article.Byline);
        if (title.Length == 0 && !string.IsNullOrWhiteSpace(article.Title))
            metadata["Title"] = Normalize(article.Title);

        var fragment = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(article.Content);
        RemoveNonContent(fragment);
        return fragment.Body?.InnerHtml ?? string.Empty;
    }

    /// <summary>Drops what is never text and keeps the text of links without their targets.</summary>
    private static void RemoveNonContent(IDocument document)
    {
        foreach (var element in document.QuerySelectorAll(string.Join(",", NonContentSelectors)).ToList())
            element.Remove();

        foreach (var link in document.QuerySelectorAll("a").ToList())
        {
            var parent = link.Parent;
            if (parent is null)
                continue;
            while (link.FirstChild is { } child)
                parent.InsertBefore(child, link);
            link.Remove();
        }
    }

    private static string ToMarkdown(string html)
    {
        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
            Formatting =
            {
                RemoveComments = true,
                // Text that starts like a heading or a table row must not become one: the chunker
                // splits on headings and keeps tables whole.
                EscapeLineStarts = true,
                ListBulletChar = '-',
                OutputLineEnding = "\n",
            },
        });
        return converter.Convert(html);
    }

    /// <summary>The deepest element nesting, measured without recursion.</summary>
    internal static int MaxDepth(IElement? root)
    {
        if (root is null)
            return 0;

        int max = 0;
        var stack = new Stack<(IElement Element, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > max)
                max = depth;
            if (max > MaxConvertibleDepth)
                return max;
            foreach (var child in element.Children)
                stack.Push((child, depth + 1));
        }

        return max;
    }

    private static bool HasTopLevelHeading(string markdown) =>
        markdown.StartsWith("# ", StringComparison.Ordinal) || markdown.Contains("\n# ", StringComparison.Ordinal);

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Tidy(string markdown) =>
        ExtraBlankLines.Replace(markdown.Replace("\r\n", "\n"), "\n\n").Trim();
}
