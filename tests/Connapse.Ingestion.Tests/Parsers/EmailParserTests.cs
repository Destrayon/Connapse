using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MimeKit;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
public class EmailParserTests
{
    private readonly EmailParser _parser;

    public EmailParserTests() => _parser = Create(new UploadSettings());

    private static EmailParser Create(UploadSettings settings)
    {
        var monitor = Substitute.For<IOptionsMonitor<UploadSettings>>();
        monitor.CurrentValue.Returns(settings);
        IDocumentParser[] others = [new TextParser(), new PdfParser(), new OfficeParser(), new HtmlParser(), new EpubParser()];
        EmailParser? parser = null;
        parser = new EmailParser(() => [.. others, parser!], monitor);
        return parser;
    }

    private static MimeMessage Message(string subject, string? text = "Body text.", string? html = null, Action<BodyBuilder>? attach = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ada Lovelace", "ada@example.com"));
        message.To.Add(new MailboxAddress("Charles Babbage", "charles@example.com"));
        message.Cc.Add(new MailboxAddress("Mary Somerville", "mary@example.com"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 9, 30, 14, 5, 0, TimeSpan.Zero);

        var builder = new BodyBuilder { TextBody = text, HtmlBody = html };
        attach?.Invoke(builder);
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static byte[] Bytes(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    private async Task<ParsedDocument> ParseAsync(byte[] bytes, string fileName = "mail.eml")
    {
        using var stream = new MemoryStream(bytes);
        return await _parser.ParseAsync(stream, fileName);
    }

    [Fact]
    public void SupportedExtensions_AreEmlAndMsg()
    {
        _parser.SupportedExtensions.Should().BeEquivalentTo([".eml", ".msg"]);
    }

    [Fact]
    public async Task ParseAsync_Eml_WritesSubjectHeaderBlockAndBody()
    {
        var result = await ParseAsync(Bytes(Message("Quarterly freight report", "Freight volumes rose 4% in September.")));

        result.Content.Should().StartWith("# Quarterly freight report");
        result.Content.Should().Contain("**From:** ").And.Contain("Ada Lovelace").And.Contain("ada@example.com");
        result.Content.Should().Contain("**To:** ").And.Contain("charles@example.com");
        result.Content.Should().Contain("**Cc:** ").And.Contain("mary@example.com");
        result.Content.Should().Contain("**Date:** 2026-09-30 14:05 +00:00");
        result.Content.Should().Contain("Freight volumes rose 4% in September.");
        result.Metadata["Title"].Should().Be("Quarterly freight report");
        result.Metadata["FileType"].Should().Be("Email");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_EmlWithHtmlOnly_ConvertsTheHtmlBody()
    {
        var html = "<html><body><p>See the rates:</p><table><tr><th>Lane</th><th>Rate</th></tr><tr><td>North</td><td>4.5%</td></tr></table></body></html>";

        var result = await ParseAsync(Bytes(Message("Rates", text: null, html: html)));

        result.Content.Should().Contain("See the rates:");
        result.Content.Should().MatchRegex(@"\|\s*North\s*\|\s*4\.5%\s*\|");
        result.Content.Should().NotContain("<td>");
    }

    [Fact]
    public async Task ParseAsync_QuotedReplyAndHashLines_AreEscaped()
    {
        var result = await ParseAsync(Bytes(Message("Re: dock", "Agreed.\n> earlier message\n# not a heading")));

        result.Content.Should().Contain("\\> earlier message");
        result.Content.Split('\n').Should().NotContain("# not a heading");
    }

    [Fact]
    public async Task ParseAsync_SupportedAttachments_AreParsedUnderTheirOwnHeading()
    {
        var result = await ParseAsync(Bytes(Message("Plans", attach: b =>
        {
            b.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes("Crane maintenance moves to Thursday."));
            b.Attachments.Add("plan.html", Encoding.UTF8.GetBytes("<html><body><h1>Plan</h1><h2>Week one</h2><p>Survey the berth.</p></body></html>"));
        })));

        result.Content.Should().Contain("## Attachment: notes.txt");
        result.Content.Should().Contain("Crane maintenance moves to Thursday.");
        result.Content.Should().Contain("## Attachment: plan.html");
        result.Content.Should().Contain("### Plan", "an attachment's headings nest under its own");
        result.Content.Should().Contain("#### Week one");
        result.Metadata["AttachmentCount"].Should().Be("2");
    }

    [Fact]
    public async Task ParseAsync_ImageAndUnsupportedAttachments_AreSkippedWithWarnings()
    {
        var result = await ParseAsync(Bytes(Message("Photos", attach: b =>
        {
            b.Attachments.Add("site.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], new ContentType("image", "png"));
            b.Attachments.Add("bundle.zip", [0x50, 0x4B, 0x03, 0x04]);
        })));

        result.Content.Should().NotContain("Attachment: site.png").And.NotContain("Attachment: bundle.zip");
        result.Warnings.Should().Contain(w => w.Contains("image attachment 'site.png'"));
        result.Warnings.Should().Contain(w => w.Contains("'bundle.zip'") && w.Contains("not supported"));
    }

    [Fact]
    public async Task ParseAsync_AttachmentWhoseContentContradictsItsExtension_IsSkipped()
    {
        // The pipeline's sniffer only sees the message, so the parser applies it to attachments.
        var result = await ParseAsync(Bytes(Message("Report", attach: b =>
            b.Attachments.Add("report.pdf", Encoding.UTF8.GetBytes("plain words, not a PDF")))));

        result.Content.Should().NotContain("Attachment: report.pdf");
        result.Warnings.Should().Contain(w => w.Contains("'report.pdf'") && w.Contains("PDF header"));
    }

    [Fact]
    public async Task ParseAsync_AttachedMessages_AreReadToTheDepthCap()
    {
        MimeMessage Wrap(MimeMessage inner, string subject) =>
            Message(subject, $"Layer {subject}.", attach: b => b.Attachments.Add(new MessagePart { Message = inner }));

        var deepest = Message("Level 4", "Layer Level 4.");
        var message = Wrap(Wrap(Wrap(deepest, "Level 3"), "Level 2"), "Level 1");

        var result = await ParseAsync(Bytes(message));

        result.Content.Should().Contain("Layer Level 1.").And.Contain("Layer Level 2.").And.Contain("Layer Level 3.");
        result.Content.Should().NotContain("Layer Level 4.");
        result.Content.Should().Contain("### Level 2", "an attached message's subject nests under its attachment heading");
        result.Warnings.Should().Contain(w => w.Contains("nested more than 3 deep"));
    }

    [Fact]
    public async Task ParseAsync_BytesThatAreNotAMessage_YieldNoContent()
    {
        var result = await ParseAsync([0x00, 0x01, 0x02]);

        result.Content.Should().BeEmpty();
        result.Warnings.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("forwarded.msg")]
    [InlineData("forwarded.eml")]
    public async Task ParseAsync_UnreadableAttachedMessage_KeepsTheRestOfTheEmail(string name)
    {
        var result = await ParseAsync(Bytes(Message("Fwd", "The parent body survives.", attach: b =>
        {
            b.Attachments.Add(name, new byte[] { 0x00, 0x01, 0x02, 0x03 });
            b.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes("So does this attachment."));
        })));

        result.Content.Should().Contain("The parent body survives.").And.Contain("So does this attachment.");
        result.Warnings.Should().Contain(w => w.Contains($"'{name}'"));
    }

    [Fact]
    public async Task ParseAsync_DrmProtectedEpubAttachment_CostsOnlyItself()
    {
        // EpubParser fails a DRM book permanently; inside a message that is one skipped attachment.
        byte[] locked = EpubParserTests.Epub("Locked", "Author", [("ch1.xhtml", "<p>Ciphertext.</p>")], extra: zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry("META-INF/encryption.xml").Open());
            writer.Write("<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\"><enc:EncryptedData><enc:EncryptionMethod Algorithm=\"http://www.w3.org/2001/04/xmlenc#aes128-cbc\"/><enc:CipherData><enc:CipherReference URI=\"OEBPS/ch1.xhtml\"/></enc:CipherData></enc:EncryptedData></encryption>");
        });

        var result = await ParseAsync(Bytes(Message("Reading list", "The body survives.", attach: b =>
        {
            b.Attachments.Add("locked.epub", locked);
            b.Attachments.Add("notes.txt", Encoding.UTF8.GetBytes("So does the other attachment."));
        })));

        result.Content.Should().Contain("The body survives.").And.Contain("So does the other attachment.");
        result.Warnings.Should().Contain(w => w.Contains("'locked.epub'") && w.Contains("[encrypted]"));
    }

    [Fact]
    public async Task ParseAsync_MoreThanTheAttachmentCap_SkipsTheRestWithAWarning()
    {
        var result = await ParseAsync(Bytes(Message("Many", attach: b =>
        {
            for (int i = 0; i < EmailParser.MaxAttachments + 5; i++)
                b.Attachments.Add($"part{i}.txt", Encoding.UTF8.GetBytes($"Part {i}."));
        })));

        result.Content.Should().Contain($"Part {EmailParser.MaxAttachments - 1}.").And.NotContain($"Part {EmailParser.MaxAttachments}.");
        result.Warnings.Should().Contain(w => w.Contains("Skipped 5 attachments"));
        result.Metadata["AttachmentCount"].Should().Be((EmailParser.MaxAttachments + 5).ToString());
    }

    [Fact]
    public async Task ParseAsync_AttachmentOverTheUploadLimit_IsSkippedBeforeItIsParsed()
    {
        var parser = Create(new UploadSettings { MaxFileBytes = 1_000 });
        byte[] bytes = Bytes(Message("Big", attach: b => b.Attachments.Add("big.txt", new byte[5_000])));

        using var stream = new MemoryStream(bytes);
        var result = await parser.ParseAsync(stream, "mail.eml");

        result.Content.Should().NotContain("Attachment: big.txt");
        result.Warnings.Should().Contain(w => w.Contains("'big.txt'") && w.Contains("[file_too_large]"));
    }

    [Fact]
    public async Task ParseAsync_AttachmentsOverTheMessageBudget_StopAtIt()
    {
        var parser = Create(new UploadSettings { MaxFileBytes = 1_000, MaxDecompressedBytes = 1_500 });
        byte[] bytes = Bytes(Message("Budget", attach: b =>
        {
            b.Attachments.Add("one.txt", Encoding.UTF8.GetBytes(new string('a', 800)));
            b.Attachments.Add("two.txt", Encoding.UTF8.GetBytes(new string('b', 800)));
        }));

        using var stream = new MemoryStream(bytes);
        var result = await parser.ParseAsync(stream, "mail.eml");

        result.Content.Should().Contain("Attachment: one.txt").And.NotContain("Attachment: two.txt");
        result.Warnings.Should().Contain(w => w.Contains("'two.txt'") && w.Contains("[decompressed_too_large]"));
    }

    [Fact]
    public async Task ParseAsync_BodySplitAroundAnAttachment_KeepsBothHalves()
    {
        // Apple Mail writes an inline attachment between two text parts of one body.
        var message = Message("Split", text: null);
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "First half of the note." },
            new MimePart("application", "octet-stream") { Content = new MimeContent(new MemoryStream([1, 2, 3])), FileName = "blob.bin" },
            new TextPart("plain") { Text = "Second half of the note." },
        };

        var result = await ParseAsync(Bytes(message));

        result.Content.Should().Contain("First half of the note.").And.Contain("Second half of the note.");
    }

    [Fact]
    public async Task ParseAsync_IndentedHeadingsInBodyOrTextAttachments_AreEscaped()
    {
        var result = await ParseAsync(Bytes(Message("Injection", "Hello.\n   # injected heading", attach: b =>
            b.Attachments.Add("plain.txt", Encoding.UTF8.GetBytes("# not a heading either")))));

        var lines = result.Content.Split('\n');
        lines.Should().Contain("   \\# injected heading");
        lines.Should().Contain("\\# not a heading either");
        lines.Should().NotContain(l => l.TrimStart().StartsWith("# inj") || l.StartsWith("### not"));
    }

    private static byte[] Msg(Action<MsgKit.Email> build)
    {
        using var email = new MsgKit.Email(new MsgKit.Sender("ada@example.com", "Ada Lovelace"), "Outlook freight report");
        email.Recipients.AddTo("charles@example.com", "Charles Babbage");
        email.Recipients.AddCc("mary@example.com", "Mary Somerville");
        email.SentOn = new DateTime(2026, 9, 30, 14, 5, 0, DateTimeKind.Utc);
        build(email);
        using var stream = new MemoryStream();
        email.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task ParseAsync_Msg_WritesHeaderBlockBodyAndAttachments()
    {
        byte[] bytes = Msg(email =>
        {
            email.BodyText = "Barge traffic doubled on the canal.";
            email.Attachments.Add(new MemoryStream(Encoding.UTF8.GetBytes("Lock 7 closes for repairs.")), "lock.txt");
        });

        var result = await ParseAsync(bytes, "report.msg");

        result.Content.Should().StartWith("# Outlook freight report");
        result.Content.Should().Contain("Ada Lovelace <ada@example.com>");
        result.Content.Should().Contain("**To:** Charles Babbage <charles@example.com>");
        result.Content.Should().Contain("**Cc:** Mary Somerville <mary@example.com>");
        result.Content.Should().Contain("Barge traffic doubled on the canal.");
        result.Content.Should().Contain("## Attachment: lock.txt");
        result.Content.Should().Contain("Lock 7 closes for repairs.");
    }

    [Fact]
    public async Task ParseAsync_MsgWithHtmlOnly_ConvertsTheHtmlBody()
    {
        byte[] bytes = Msg(email => email.BodyHtml = "<html><body><h2>Schedule</h2><ul><li>Monday: survey</li></ul></body></html>");

        var result = await ParseAsync(bytes, "report.msg");

        result.Content.Should().Contain("Schedule").And.Contain("Monday: survey");
        result.Content.Should().NotContain("<li>");
    }

    [Theory]
    [InlineData("# Title\n## Section\ntext", "### Title\n#### Section\ntext")]
    [InlineData("##### Deep", "###### Deep")]
    [InlineData("#hashtag\n\\# escaped", "#hashtag\n\\# escaped")]
    [InlineData("```\n# comment in code\n```\n# Real", "```\n# comment in code\n```\n### Real")]
    [InlineData("  # Indented", "### Indented")]
    public void DemoteHeadings_ShiftsOnlyRealHeadings(string markdown, string expected)
    {
        MarkdownText.DemoteHeadings(markdown, 2).Should().Be(expected);
    }

    [Theory]
    [InlineData("# heading", "\\# heading")]
    [InlineData("   # heading", "   \\# heading")]
    [InlineData("  > quote", "  \\> quote")]
    [InlineData("    # code block", "    # code block")]
    [InlineData("plain", "plain")]
    public void EscapeLine_EscapesMarkersAfterUpToThreeSpaces(string line, string expected)
    {
        MarkdownText.EscapeLine(line).Should().Be(expected);
    }
}
