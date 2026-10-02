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
/// what people search mail by. A MIME body is every inline text part in order, taking the plain
/// version of each alternative; an Outlook body is the plain text, else the HTML, else the RTF.
/// HTML is converted whole, without Readability, which would score a message as a page.
/// </para>
/// <para>
/// Attachments are parsed by the parsers registered for their extensions, under the same checks
/// the pipeline applies to an uploaded file -- the content must match its extension, stay within
/// the size limits and not come out garbled -- since an attachment is an uploaded file that
/// arrived inside another. Each is decoded only when its turn comes, into a buffer capped by the
/// upload limit and by what is left of one budget for the whole message, and one that fails is
/// skipped with a warning rather than costing the message. Attached messages are read a few levels
/// deep. Images and types no parser handles are skipped and listed in the warnings.
/// </para>
/// </summary>
public class EmailParser(
    Func<IEnumerable<IDocumentParser>> parsers,
    IOptionsMonitor<UploadSettings>? limits = null) : IDocumentParser
{
    /// <summary>How many messages deep attached messages are read, the message itself being 1.</summary>
    internal const int MaxNestingDepth = 3;

    /// <summary>Attachments read per message; the rest are counted and listed as skipped.</summary>
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

    /// <summary>
    /// Attachments whose parser writes Markdown. Anything else -- plain text, CSV, JSON -- is raw
    /// text, and inside a message that the DocumentAware chunker reads as Markdown its "#" lines
    /// would become headings, so it is escaped line by line.
    /// </summary>
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".pdf", ".docx", ".pptx", ".html", ".htm", ".epub",
    };

    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant);

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public int Version => 1;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var metadata = new Dictionary<string, string> { ["FileType"] = "Email" };
        var context = new RenderContext(limits?.CurrentValue ?? new UploadSettings());

        try
        {
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            return await Task.Run(async () =>
            {
                var mail = Read(stream, extension, depth: 1, context, cancellationToken);

                if (mail.Subject.Length > 0)
                    metadata["Title"] = mail.Subject;
                if (mail.From.Length > 0)
                    metadata["From"] = mail.From;
                if (mail.Date is { } date)
                    metadata["Date"] = date.ToString("O");
                metadata["AttachmentCount"] = (mail.Attachments.Count + mail.SkippedAttachments).ToString();

                // MimeKit reads any bytes as a message; one with nothing in it is not a document.
                bool isEmpty = mail is { Subject: "", From: "", To: "", Body: "", Attachments.Count: 0 };
                string content = isEmpty ? string.Empty : await RenderAsync(mail, context, cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                {
                    context.Warnings.Add("Document contains no readable text content");
                    content = string.Empty;
                }

                return new ParsedDocument(content, metadata, context.Warnings);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Warnings.Add($"Error parsing email: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, context.Warnings);
        }
        finally
        {
            context.Dispose();
        }
    }

    /// <summary>What one parse shares: its warnings, its attachment byte budget, and the open .msg files.</summary>
    private sealed class RenderContext(UploadSettings settings) : IDisposable
    {
        public UploadSettings Settings { get; } = settings;
        public List<string> Warnings { get; } = [];
        public long RemainingBytes { get; set; } = settings.MaxDecompressedBytes;

        // MsgReader reads attachment data from the compound file, so it stays open until rendered.
        public List<IDisposable> Open { get; } = [];

        public void Dispose()
        {
            foreach (var item in Open)
                item.Dispose();
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
        IReadOnlyList<MailAttachment> Attachments,
        int SkippedAttachments);

    /// <summary>
    /// An attached file, decoded on demand by Load into at most the given number of bytes; an
    /// attached message already read (Message); or one past the depth cap (TooDeep).
    /// </summary>
    private sealed record MailAttachment(
        string Name,
        string? MimeType,
        Func<long, byte[]?>? Load,
        Mail? Message = null,
        bool TooDeep = false);

    /// <summary>Thrown by an attachment's Load when it decodes to more than it was allowed.</summary>
    private sealed class AttachmentTooLargeException : Exception;

    private static Mail Read(Stream stream, string extension, int depth, RenderContext context, CancellationToken ct)
    {
        switch (extension)
        {
            case ".eml":
                var options = ParserOptions.Default.Clone();
                options.MaxMimeDepth = MaxMimeDepth;
                return new MimeReader(depth, context, ct).Read(MimeMessage.Load(options, stream, ct));
            case ".msg":
                var message = new OutlookStorage.Message(stream, FileAccess.Read, true);
                context.Open.Add(message);
                return FromOutlook(message, depth, context, ct);
            default:
                throw new NotSupportedException($"Extension {extension} is not supported by EmailParser");
        }
    }

    /// <summary>
    /// Walks a MIME message's parts in order: inline text is body, the plain version of each
    /// alternative is taken, inline images are the HTML's own, and everything else is an attachment.
    /// MimeKit's Attachments list holds only parts marked as attachments, so a message forwarded
    /// inline or a PDF sent inline would be dropped; its TextBody is a single part, so a body split
    /// around an attachment would lose its second half.
    /// </summary>
    private sealed class MimeReader(int depth, RenderContext context, CancellationToken ct)
    {
        private readonly List<string> _body = [];
        private readonly List<MailAttachment> _attachments = [];
        private int _skipped;

        public Mail Read(MimeMessage message)
        {
            if (message.Body is { } root)
                Walk(root);

            return new Mail(
                depth,
                Normalize(message.Subject),
                Normalize(message.From?.ToString()),
                Normalize(message.To?.ToString()),
                Normalize(message.Cc?.ToString()),
                message.Date == DateTimeOffset.MinValue ? null : message.Date,
                string.Join("\n\n", _body.Where(b => b.Length > 0)),
                _attachments,
                _skipped);
        }

        private void Walk(MimeEntity entity)
        {
            ct.ThrowIfCancellationRequested();
            switch (entity)
            {
                case MultipartAlternative alternative:
                    // Plain text when the sender wrote it; otherwise the richest version, last.
                    if (alternative.OfType<TextPart>().LastOrDefault(t => t.IsPlain && !string.IsNullOrWhiteSpace(t.Text)) is { } plain)
                        Walk(plain);
                    else if (alternative.Count > 0)
                        Walk(alternative[alternative.Count - 1]);
                    break;
                case Multipart multipart:
                    foreach (var child in multipart)
                        Walk(child);
                    break;
                case MessagePart { Message: { } nested } part:
                    string name = part.ContentDisposition?.FileName ?? $"{OrDefault(nested.Subject, "message")}.eml";
                    Add(depth >= MaxNestingDepth
                        ? new MailAttachment(name, "message/rfc822", null, TooDeep: true)
                        : new MailAttachment(name, "message/rfc822", null, new MimeReader(depth + 1, context, ct).Read(nested)));
                    break;
                case TnefPart tnef:
                    // winmail.dat: Outlook's own wrapper, which holds the real attachments.
                    foreach (var inner in tnef.ExtractAttachments().OfType<MimePart>())
                        Add(FromMimePart(inner));
                    break;
                case TextPart { IsAttachment: false } text when text.IsPlain:
                    _body.Add(PlainBody(text.Text));
                    break;
                case TextPart { IsAttachment: false } text when text.IsHtml:
                    _body.Add(HtmlParser.BodyToMarkdown(text.Text, context.Warnings, ct));
                    break;
                case TextPart { IsAttachment: false } text when text.IsRichText:
                    _body.Add(HtmlParser.BodyToMarkdown(RtfPipe.Rtf.ToHtml(text.Text), context.Warnings, ct));
                    break;
                case MimePart { IsAttachment: false } inline when inline.ContentType.IsMimeType("image", "*"):
                    break;
                case MimePart part:
                    Add(FromMimePart(part));
                    break;
            }
        }

        private void Add(MailAttachment attachment)
        {
            if (_attachments.Count < MaxAttachments)
                _attachments.Add(attachment);
            else
                _skipped++;
        }
    }

    private static MailAttachment FromMimePart(MimePart part)
    {
        string mimeType = part.ContentType.MimeType;
        string name = part.FileName
            ?? (MimeTypes.TryGetExtension(mimeType, out string? ext) ? "attachment" + ext : "attachment");

        return new MailAttachment(name, mimeType, max =>
        {
            if (part.Content is null)
                return null;
            using var decoded = new BoundedMemoryStream(max);
            part.Content.DecodeTo(decoded);
            return decoded.ToArray();
        });
    }

    private static Mail FromOutlook(OutlookStorage.Message message, int depth, RenderContext context, CancellationToken ct)
    {
        string body;
        if (!string.IsNullOrWhiteSpace(message.BodyText))
            body = PlainBody(message.BodyText);
        else if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            body = HtmlParser.BodyToMarkdown(message.BodyHtml, context.Warnings, ct);
        else if (!string.IsNullOrWhiteSpace(message.BodyRtf))
            body = HtmlParser.BodyToMarkdown(RtfPipe.Rtf.ToHtml(message.BodyRtf), context.Warnings, ct);
        else
            body = string.Empty;

        var attachments = new List<MailAttachment>();
        int skipped = 0;
        foreach (object item in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (attachments.Count >= MaxAttachments)
            {
                skipped++;
                continue;
            }

            switch (item)
            {
                case OutlookStorage.Message nested:
                    string name = $"{OrDefault(nested.Subject, "message")}.msg";
                    attachments.Add(depth >= MaxNestingDepth
                        ? new MailAttachment(name, "application/vnd.ms-outlook", null, TooDeep: true)
                        : new MailAttachment(name, "application/vnd.ms-outlook", null, FromOutlook(nested, depth + 1, context, ct)));
                    break;
                case OutlookStorage.Attachment file when !file.IsContactPhoto:
                    attachments.Add(new MailAttachment(OrDefault(file.FileName, "attachment"), file.MimeType, max =>
                    {
                        byte[]? data = file.Data;
                        return data is null ? null : data.Length > max ? throw new AttachmentTooLargeException() : data;
                    }));
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
            attachments,
            skipped);
    }

    private async Task<string> RenderAsync(Mail mail, RenderContext context, CancellationToken ct)
    {
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

        foreach (var attachment in mail.Attachments)
        {
            ct.ThrowIfCancellationRequested();

            string? section;
            try
            {
                section = await RenderAttachmentAsync(attachment, mail.Depth, context, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable attachment costs itself, not the message and its other attachments.
                context.Warnings.Add($"Skipped attachment '{attachment.Name}': it could not be read ({ex.Message}).");
                continue;
            }

            if (section is { Length: > 0 })
            {
                text.Append("## Attachment: ").Append(MarkdownText.EscapeLine(attachment.Name)).Append("\n\n")
                    .Append(MarkdownText.DemoteHeadings(section, 2)).Append("\n\n");
            }
        }

        if (mail.SkippedAttachments > 0)
            context.Warnings.Add($"Skipped {mail.SkippedAttachments} attachments past the first {MaxAttachments}.");

        return ExtraBlankLines.Replace(text.ToString(), "\n\n").Trim();
    }

    private async Task<string?> RenderAttachmentAsync(MailAttachment attachment, int depth, RenderContext context, CancellationToken ct)
    {
        string name = attachment.Name;
        string extension = Path.GetExtension(name).ToLowerInvariant();

        if (attachment.Message is { } message)
            return await RenderAsync(message, context, ct);

        bool isMessageFile = _supportedExtensions.Contains(extension);
        if (attachment.TooDeep || (isMessageFile && depth >= MaxNestingDepth))
        {
            context.Warnings.Add($"Skipped attached message '{name}': messages nested more than {MaxNestingDepth} deep are not read.");
            return null;
        }

        if (attachment.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || ImageExtensions.Contains(extension))
        {
            context.Warnings.Add($"Skipped image attachment '{name}'.");
            return null;
        }

        var parser = isMessageFile ? null : parsers().FirstOrDefault(p => p is not EmailParser && p.SupportedExtensions.Contains(extension));
        if ((!isMessageFile && parser is null) || attachment.Load is null)
        {
            context.Warnings.Add($"Skipped attachment '{name}': its type is not supported.");
            return null;
        }

        // Decoded only now, into at most the upload limit and what the message's budget has left.
        long allowed = Math.Min(context.Settings.MaxFileBytes, context.RemainingBytes);
        byte[]? data;
        try
        {
            data = attachment.Load(allowed);
        }
        catch (AttachmentTooLargeException)
        {
            context.Warnings.Add(allowed < context.Settings.MaxFileBytes
                ? $"Skipped attachment '{name}': the message's attachments together exceed the {Megabytes(context.Settings.MaxDecompressedBytes)} MB limit [decompressed_too_large]"
                : $"Skipped attachment '{name}': it is over the {Megabytes(context.Settings.MaxFileBytes)} MB limit [file_too_large]");
            return null;
        }

        if (data is null)
        {
            context.Warnings.Add($"Skipped attachment '{name}': it has no content.");
            return null;
        }

        context.RemainingBytes -= data.Length;

        // The checks the pipeline makes on an upload, made here because the pipeline only sees
        // the message.
        using var content = new MemoryStream(data, writable: false);
        var head = data.AsSpan(0, Math.Min(data.Length, ContentSniffer.HeaderLength));
        if ((ContentSniffer.DescribeMismatch(head, extension) ?? ParseLimits.CheckInput(content, extension, context.Settings)) is { } refusal)
        {
            context.Warnings.Add($"Skipped attachment '{name}': {refusal}");
            return null;
        }

        if (isMessageFile)
            return await RenderAsync(Read(content, extension, depth + 1, context, ct), context, ct);

        var parsed = await parser!.ParseAsync(content, name, ct);
        context.Warnings.AddRange(parsed.Warnings.Select(w => $"Attachment '{name}': {w}"));

        if (TextQuality.DescribeGarbled(extension, TextQuality.Measure(parsed.Content), context.Settings) is { } garbled)
        {
            context.Warnings.Add($"Skipped attachment '{name}': {garbled}");
            return null;
        }

        return MarkdownExtensions.Contains(extension) ? parsed.Content.Trim() : PlainBody(parsed.Content);
    }

    private static void AppendHeader(StringBuilder text, string label, string value)
    {
        if (value.Length > 0)
            text.Append("**").Append(label).Append(":** ").Append(value).Append('\n');
    }

    /// <summary>
    /// Text as Markdown paragraphs, with lines that start like Markdown structure escaped: a quoted
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

    private static long Megabytes(long bytes) => bytes / (1024 * 1024);

    /// <summary>A MemoryStream that refuses to grow past a limit, so decoding stops at it.</summary>
    private sealed class BoundedMemoryStream(long max) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Check(int count)
        {
            if (Length + count > max)
                throw new AttachmentTooLargeException();
        }
    }
}
