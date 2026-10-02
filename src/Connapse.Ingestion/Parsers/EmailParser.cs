using System.Text;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Validation;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Tnef;
using MsgReader.Outlook;
using OutlookStorage = MsgReader.Outlook.Storage;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for email messages, .eml (MIME, read with MimeKit) and .msg (Outlook, read with
/// MsgReader), written out as Markdown (#600).
/// <para>
/// The subject becomes the heading and From, To, Cc and Date a header block, because those are
/// what people search mail by. The body is the plain-text part when there is one, else the HTML
/// part converted whole, else the RTF body converted to HTML first.
/// </para>
/// <para>
/// Attachments are parsed by the parsers registered for their extensions, under the same checks
/// the pipeline applies to an uploaded file -- the content must match its extension, stay within
/// the size limits and not come out garbled -- since an attachment is an uploaded file that
/// arrived inside another. Attached messages are read recursively, a few levels deep. Images and
/// types no parser handles are skipped and listed in the warnings.
/// </para>
/// </summary>
public class EmailParser(
    Func<IEnumerable<IDocumentParser>> parsers,
    IOptionsMonitor<UploadSettings>? limits = null) : IDocumentParser
{
    /// <summary>How many messages deep attached messages are read, the message itself being 1.</summary>
    internal const int MaxNestingDepth = 3;

    /// <summary>Attachments read per message; the rest are listed as skipped.</summary>
    internal const int MaxAttachments = 100;

    /// <summary>
    /// MIME nesting MimeKit parses into parts; deeper content stays one opaque part. Its own
    /// default is 1024, and the parser recurses per level.
    /// </summary>
    private const int MaxMimeDepth = 64;

    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".eml",
        ".msg",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".heif", ".svg", ".ico", ".emf", ".wmf",
    };

    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant);

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public int Version => 1;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string> { ["FileType"] = "Email" };

        try
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            var mail = await Task.Run(() => Read(stream, extension, depth: 1, cancellationToken), cancellationToken);

            if (mail.Subject.Length > 0)
                metadata["Title"] = mail.Subject;
            if (mail.From.Length > 0)
                metadata["From"] = mail.From;
            if (mail.Date is { } date)
                metadata["Date"] = date.ToString("O");
            metadata["AttachmentCount"] = mail.Attachments.Count.ToString();

            // MimeKit reads any bytes as a message; one with nothing in it is not a document.
            bool isEmpty = mail is { Subject: "", From: "", To: "", Body: "", Attachments.Count: 0 };
            string content = isEmpty ? string.Empty : await RenderAsync(mail, warnings, cancellationToken);
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
            warnings.Add($"Error parsing email: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    /// <summary>A message, whichever format it came from, at its depth in the attachment tree.</summary>
    private sealed record Mail(
        int Depth,
        string Subject,
        string From,
        string To,
        string Cc,
        DateTimeOffset? Date,
        string Body,
        List<string> BodyWarnings,
        IReadOnlyList<MailAttachment> Attachments);

    /// <summary>
    /// An attached file (Data), an attached message already read (Message), or an attached message
    /// past the depth cap (TooDeep), which is listed and not read.
    /// </summary>
    private sealed record MailAttachment(string Name, string? MimeType, byte[]? Data, Mail? Message = null, bool TooDeep = false);

    private static Mail Read(Stream stream, string extension, int depth, CancellationToken ct)
    {
        switch (extension)
        {
            case ".eml":
                var options = ParserOptions.Default.Clone();
                options.MaxMimeDepth = MaxMimeDepth;
                return FromMime(MimeMessage.Load(options, stream, ct), depth, ct);
            case ".msg":
                using (var message = new OutlookStorage.Message(stream, FileAccess.Read, true))
                    return FromOutlook(message, depth, ct);
            default:
                throw new NotSupportedException($"Extension {extension} is not supported by EmailParser");
        }
    }

    private static Mail FromMime(MimeMessage message, int depth, CancellationToken ct)
    {
        var bodyWarnings = new List<string>();
        string body = !string.IsNullOrWhiteSpace(message.TextBody)
            ? PlainBody(message.TextBody)
            : message.HtmlBody is { } html ? HtmlParser.BodyToMarkdown(html, bodyWarnings, ct) : string.Empty;

        // Every part, not message.Attachments: that list holds only parts marked as attachments,
        // and a message forwarded inline or a PDF sent inline would be dropped without a word.
        var attachments = new List<MailAttachment>();
        foreach (var entity in message.BodyParts)
        {
            ct.ThrowIfCancellationRequested();
            switch (entity)
            {
                case MimePart { IsAttachment: false } inline when inline is not TnefPart
                    && (inline.ContentType.IsMimeType("text", "*") || inline.ContentType.IsMimeType("image", "*")):
                    // The body itself, or an image the HTML body shows: neither is an attachment.
                    break;
                case MessagePart { Message: { } nested } part:
                    string name = part.ContentDisposition?.FileName ?? $"{OrDefault(nested.Subject, "message")}.eml";
                    attachments.Add(depth >= MaxNestingDepth
                        ? new MailAttachment(name, "message/rfc822", null, TooDeep: true)
                        : new MailAttachment(name, "message/rfc822", null, FromMime(nested, depth + 1, ct)));
                    break;
                case TnefPart tnef:
                    // winmail.dat: Outlook's own wrapper, which holds the real attachments.
                    foreach (var inner in tnef.ExtractAttachments().OfType<MimePart>())
                        attachments.Add(FromMimePart(inner));
                    break;
                case MimePart part:
                    attachments.Add(FromMimePart(part));
                    break;
            }
        }

        return new Mail(
            depth,
            Normalize(message.Subject),
            Normalize(message.From?.ToString()),
            Normalize(message.To?.ToString()),
            Normalize(message.Cc?.ToString()),
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            body,
            bodyWarnings,
            attachments);
    }

    private static MailAttachment FromMimePart(MimePart part)
    {
        string mimeType = part.ContentType.MimeType;
        string name = part.FileName
            ?? (MimeTypes.TryGetExtension(mimeType, out string? ext) ? "attachment" + ext : "attachment");

        using var decoded = new MemoryStream();
        part.Content?.DecodeTo(decoded);
        return new MailAttachment(name, mimeType, decoded.ToArray());
    }

    private static Mail FromOutlook(OutlookStorage.Message message, int depth, CancellationToken ct)
    {
        var bodyWarnings = new List<string>();
        string body;
        if (!string.IsNullOrWhiteSpace(message.BodyText))
            body = PlainBody(message.BodyText);
        else if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            body = HtmlParser.BodyToMarkdown(message.BodyHtml, bodyWarnings, ct);
        else if (!string.IsNullOrWhiteSpace(message.BodyRtf))
            body = HtmlParser.BodyToMarkdown(RtfPipe.Rtf.ToHtml(message.BodyRtf), bodyWarnings, ct);
        else
            body = string.Empty;

        var attachments = new List<MailAttachment>();
        foreach (object item in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            switch (item)
            {
                case OutlookStorage.Message nested:
                    string name = $"{OrDefault(nested.Subject, "message")}.msg";
                    attachments.Add(depth >= MaxNestingDepth
                        ? new MailAttachment(name, "application/vnd.ms-outlook", null, TooDeep: true)
                        : new MailAttachment(name, "application/vnd.ms-outlook", null, FromOutlook(nested, depth + 1, ct)));
                    break;
                case OutlookStorage.Attachment file when !file.IsContactPhoto:
                    attachments.Add(new MailAttachment(OrDefault(file.FileName, "attachment"), file.MimeType, file.Data));
                    break;
            }
        }

        string Recipients(RecipientType type) => Normalize(string.Join(", ", message.Recipients
            .Where(r => r.Type == type)
            .Select(r => Address(r.DisplayName, r.Email))));

        return new Mail(
            depth,
            Normalize(message.Subject),
            Normalize(message.Sender is { } sender ? Address(sender.DisplayName, sender.Email) : null),
            Recipients(RecipientType.To),
            Recipients(RecipientType.Cc),
            message.SentOn,
            body,
            bodyWarnings,
            attachments);
    }

    private async Task<string> RenderAsync(Mail mail, List<string> warnings, CancellationToken ct)
    {
        warnings.AddRange(mail.BodyWarnings);

        var text = new StringBuilder();
        text.Append("# ").Append(MarkdownText.EscapeLine(OrDefault(mail.Subject, "(no subject)"))).Append("\n\n");

        AppendHeader(text, "From", mail.From);
        AppendHeader(text, "To", mail.To);
        AppendHeader(text, "Cc", mail.Cc);
        if (mail.Date is { } date)
            AppendHeader(text, "Date", date.ToString("yyyy-MM-dd HH:mm zzz"));
        text.Append('\n');

        if (mail.Body.Length > 0)
            text.Append(mail.Body).Append("\n\n");

        for (int i = 0; i < mail.Attachments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i == MaxAttachments)
            {
                warnings.Add($"Skipped {mail.Attachments.Count - MaxAttachments} attachments past the first {MaxAttachments}.");
                break;
            }

            var attachment = mail.Attachments[i];
            if (await RenderAttachmentAsync(attachment, mail.Depth, warnings, ct) is { Length: > 0 } section)
            {
                text.Append("## Attachment: ").Append(MarkdownText.EscapeLine(attachment.Name)).Append("\n\n")
                    .Append(MarkdownText.DemoteHeadings(section, 2)).Append("\n\n");
            }
        }

        return ExtraBlankLines.Replace(text.ToString(), "\n\n").Trim();
    }

    private async Task<string?> RenderAttachmentAsync(MailAttachment attachment, int depth, List<string> warnings, CancellationToken ct)
    {
        string name = attachment.Name;
        string extension = Path.GetExtension(name).ToLowerInvariant();

        if (attachment.Message is { } message)
            return await RenderAsync(message, warnings, ct);

        bool isMessageFile = _supportedExtensions.Contains(extension) && attachment.Data is not null;
        if (attachment.TooDeep || (isMessageFile && depth >= MaxNestingDepth))
        {
            warnings.Add($"Skipped attached message '{name}': messages nested more than {MaxNestingDepth} deep are not read.");
            return null;
        }

        if (isMessageFile)
            return await RenderAsync(Read(new MemoryStream(attachment.Data!, writable: false), extension, depth + 1, ct), warnings, ct);

        if (attachment.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || ImageExtensions.Contains(extension))
        {
            warnings.Add($"Skipped image attachment '{name}'.");
            return null;
        }

        var parser = parsers().FirstOrDefault(p => p is not EmailParser && p.SupportedExtensions.Contains(extension));
        if (parser is null || attachment.Data is null)
        {
            warnings.Add($"Skipped attachment '{name}': its type is not supported.");
            return null;
        }

        // The checks the pipeline makes on an upload, made here because the pipeline only sees
        // the message.
        UploadSettings settings = limits?.CurrentValue ?? new UploadSettings();
        using var content = new MemoryStream(attachment.Data, writable: false);
        var head = attachment.Data.AsSpan(0, Math.Min(attachment.Data.Length, ContentSniffer.HeaderLength));
        if ((ContentSniffer.DescribeMismatch(head, extension) ?? ParseLimits.CheckInput(content, extension, settings)) is { } refusal)
        {
            warnings.Add($"Skipped attachment '{name}': {refusal}");
            return null;
        }

        var parsed = await parser.ParseAsync(content, name, ct);
        warnings.AddRange(parsed.Warnings.Select(w => $"Attachment '{name}': {w}"));

        if (TextQuality.DescribeGarbled(extension, TextQuality.Measure(parsed.Content), settings) is { } garbled)
        {
            warnings.Add($"Skipped attachment '{name}': {garbled}");
            return null;
        }

        return parsed.Content.Trim();
    }

    private static void AppendHeader(StringBuilder text, string label, string value)
    {
        if (value.Length > 0)
            text.Append("**").Append(label).Append(":** ").Append(value).Append('\n');
    }

    /// <summary>
    /// The text part, line by line, with lines that start like Markdown structure escaped: a quoted
    /// reply's "&gt;" or a "#" line must not split or retitle chunks.
    /// </summary>
    private static string PlainBody(string text) =>
        ExtraBlankLines.Replace(
            string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(l => MarkdownText.EscapeLine(l.TrimEnd()))),
            "\n\n").Trim();

    private static string Address(string? displayName, string? email) =>
        string.IsNullOrWhiteSpace(displayName) || displayName == email
            ? email ?? string.Empty
            : string.IsNullOrWhiteSpace(email) ? displayName : $"{displayName} <{email}>";

    private static string OrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
