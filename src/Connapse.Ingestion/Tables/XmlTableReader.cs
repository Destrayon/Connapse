using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Xml;
using Connapse.Core;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns an XML file into typed tables (CON-335) by reading it as the JSON it maps to and handing that to
/// <see cref="TreeFlattener"/>. The root element is the record. An element with only text is that text; one with
/// attributes or child elements is an object whose attributes are <c>@name</c> keys, whose children are keys by their
/// local name (a name repeated among siblings becomes a list, so a child table), and whose own text is the <c>#text</c>
/// key. An element mixing text with child elements, such as a paragraph with emphasis, is its whole text, under <c>#text</c>
/// beside its attributes when it has any. Every value
/// is text; the CSV loader's rules type the columns. DTDs are ignored and nothing outside the file is read, so entity
/// tricks can't reach files or blow up memory.
/// </summary>
internal static class XmlTableReader
{
    /// <summary>The key an element's own text takes beside its attributes and children.</summary>
    public const string TextKey = "#text";

    /// <summary>The deepest element nesting read; deeper is refused as unreadable rather than risking the stack.</summary>
    public const int MaxDepth = 200;

    private static readonly XmlReaderSettings Settings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <inheritdoc cref="JsonTableReader.ReadAsync"/>
    public static async Task<(IReadOnlyList<TableLoad> Tables, string? Warning, IDisposable? Owner)> ReadAsync(
        Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        if (file.Length > JsonTableReader.MaxTreeBytes)
            return ([], $"{Path.GetFileName(fileName)} is larger than {JsonTableReader.MaxTreeBytes / (1024 * 1024)} MB, too large to load as tables", null);
        file.Position = 0;
        Element? root = await ReadTreeAsync(file, ct);
        if (root is null)
            return ([], null, null);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, root);
        JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaxDepth + 8 });
        return await JsonTableReader.FlattenOwnedAsync(document, JsonTableReader.One(document.RootElement, TextKey), fileName, documentId, ct);
    }

    private sealed class Element(string name)
    {
        public string Name { get; } = name;

        public List<(string Name, string Value)> Attributes { get; } = [];

        /// <summary>Text pieces and child elements, in document order.</summary>
        public List<object> Content { get; } = [];

        public bool HasChildren { get; set; }

        public bool HasText { get; set; }
    }

    private static async Task<Element?> ReadTreeAsync(Stream file, CancellationToken ct)
    {
        using XmlReader reader = XmlReader.Create(file, Settings);
        var stack = new Stack<Element>();
        Element? root = null;
        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var element = new Element(reader.LocalName);
                    bool empty = reader.IsEmptyElement;
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.Prefix != "xmlns" && reader.Name != "xmlns")
                                element.Attributes.Add(("@" + reader.LocalName, reader.Value));
                        }
                        while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (stack.TryPeek(out Element? parent))
                    {
                        parent.Content.Add(element);
                        parent.HasChildren = true;
                    }
                    else
                    {
                        root = element;
                    }
                    if (!empty)
                    {
                        if (stack.Count >= MaxDepth)
                            throw new InvalidDataException($"The XML nests elements deeper than {MaxDepth} levels.");
                        stack.Push(element);
                    }
                    break;
                case XmlNodeType.EndElement:
                    stack.Pop();
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace:
                    if (stack.TryPeek(out Element? owner))
                    {
                        owner.Content.Add(reader.Value);
                        if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                            owner.HasText = true;
                    }
                    break;
            }
        }
        return root;
    }

    private static void Write(Utf8JsonWriter writer, Element element)
    {
        if (element.HasChildren && element.HasText)
        {
            if (element.Attributes.Count == 0)
            {
                writer.WriteStringValue(InnerText(element).Trim());
                return;
            }
            writer.WriteStartObject();
            foreach ((string name, string value) in element.Attributes)
                writer.WriteString(name, value);
            writer.WriteString(TextKey, InnerText(element).Trim());
            writer.WriteEndObject();
            return;
        }
        if (!element.HasChildren && element.Attributes.Count == 0)
        {
            if (element.HasText)
                writer.WriteStringValue(OwnText(element));
            else
                writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        foreach ((string name, string value) in element.Attributes)
            writer.WriteString(name, value);
        foreach (object item in element.Content)
        {
            if (item is Element child)
            {
                writer.WritePropertyName(child.Name);
                Write(writer, child);
            }
        }
        if (element.HasText)
            writer.WriteString(TextKey, OwnText(element));
        writer.WriteEndObject();
    }

    private static string OwnText(Element element) =>
        string.Concat(element.Content.OfType<string>()).Trim();

    private static string InnerText(Element element)
    {
        var text = new StringBuilder();
        Append(text, element);
        return text.ToString();

        static void Append(StringBuilder text, Element element)
        {
            foreach (object item in element.Content)
            {
                if (item is string piece)
                    text.Append(piece);
                else
                    Append(text, (Element)item);
            }
        }
    }
}
