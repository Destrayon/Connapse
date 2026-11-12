using System.Buffers;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns a YAML file into typed tables (CON-335) by reading it as the JSON it maps to and handing that to
/// <see cref="TreeFlattener"/>. One document is one record, loaded like a top-level JSON object, unless it is a list,
/// which loads like a top-level JSON array; a stream of several documents is one record per document, loaded like JSON
/// Lines. Plain scalars follow YAML 1.2's core schema for null
/// and true/false; every other scalar is text, typed by the CSV loader's rules, so <c>no</c> and <c>on</c> stay words.
/// Anchors and aliases are expanded and <c>&lt;&lt;</c> merge keys applied, up to <see cref="MaxValues"/> values in all,
/// so a file of nested aliases can't blow up memory.
/// </summary>
internal static class YamlTableReader
{
    /// <summary>The most values a file may expand to, aliases included.</summary>
    public const int MaxValues = 1_000_000;

    /// <summary>The deepest nesting read; deeper is refused as unreadable rather than risking the stack.</summary>
    public const int MaxDepth = 200;

    /// <inheritdoc cref="JsonTableReader.ReadAsync"/>
    public static async Task<(IReadOnlyList<TableLoad> Tables, string? Warning, IDisposable? Owner)> ReadAsync(
        Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        if (file.Length > JsonTableReader.MaxTreeBytes)
            return ([], $"{Path.GetFileName(fileName)} is larger than {JsonTableReader.MaxTreeBytes / (1024 * 1024)} MB, too large to load as tables", null);
        file.Position = 0;
        string text;
        using (var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            text = await reader.ReadToEndAsync(ct);

        List<Node> documents;
        try
        {
            documents = ReadDocuments(text, ct);
        }
        catch (Exception e) when (e is YamlException or InvalidOperationException)
        {
            throw new InvalidDataException($"The YAML can't be read: {e.Message}", e);
        }
        documents.RemoveAll(d => d is Scalar { Value: null });
        if (documents.Count == 0)
            return ([], null, null);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            int written = 0;
            if (documents.Count == 1)
            {
                Write(writer, documents[0], ref written, 0);
            }
            else
            {
                writer.WriteStartArray();
                foreach (Node document in documents)
                    Write(writer, document, ref written, 1);
                writer.WriteEndArray();
            }
        }
        JsonDocument json = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = MaxDepth + 8 });
        JsonElement root = json.RootElement;
        // A single document that is a list loads like a top-level JSON array: one record per item.
        RecordSource source = documents.Count == 1 && documents[0] is not Sequence
            ? JsonTableReader.One(root)
            : new RecordSource(_ => Items(root), singleRecord: false);
        return await JsonTableReader.FlattenOwnedAsync(json, source, fileName, documentId, ct);
    }

    private static async IAsyncEnumerable<JsonElement> Items(JsonElement array)
    {
        await Task.CompletedTask;
        foreach (JsonElement item in array.EnumerateArray())
            yield return item;
    }

    private abstract class Node;

    private sealed class Scalar(string? value, bool isText) : Node
    {
        /// <summary>The scalar's text, or null for a YAML null.</summary>
        public string? Value { get; } = value;

        /// <summary>False for a plain true or false, written as a JSON boolean.</summary>
        public bool IsText { get; } = isText;
    }

    private sealed class Sequence : Node
    {
        public List<Node> Items { get; } = [];
    }

    private sealed class Mapping : Node
    {
        public List<(string Key, Node Value)> Entries { get; } = [];
    }

    private static List<Node> ReadDocuments(string text, CancellationToken ct)
    {
        var parser = new Parser(new StringReader(text));
        var documents = new List<Node>();
        parser.Consume<StreamStart>();
        while (parser.TryConsume<DocumentStart>(out _))
        {
            ct.ThrowIfCancellationRequested();
            var anchors = new Dictionary<string, Node>(StringComparer.Ordinal);
            int read = 0;
            documents.Add(parser.Accept<DocumentEnd>(out _) ? new Scalar(null, true) : ReadNode(parser, anchors, ref read, 0));
            parser.Consume<DocumentEnd>();
        }
        parser.Consume<StreamEnd>();
        return documents;
    }

    private static Node ReadNode(IParser parser, Dictionary<string, Node> anchors, ref int read, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"The YAML nests deeper than {MaxDepth} levels.");
        if (++read > MaxValues)
            throw new InvalidDataException($"The YAML holds more than {MaxValues} values.");
        if (parser.TryConsume<AnchorAlias>(out AnchorAlias? alias))
        {
            return anchors.TryGetValue(alias.Value.Value, out Node? target)
                ? target
                : throw new InvalidDataException($"The YAML refers to an anchor it never set: {alias.Value.Value}");
        }

        Node node;
        string? anchor;
        if (parser.TryConsume<YamlDotNet.Core.Events.Scalar>(out var scalar))
        {
            anchor = scalar.Anchor.IsEmpty ? null : scalar.Anchor.Value;
            node = ToScalar(scalar);
        }
        else if (parser.TryConsume<SequenceStart>(out var sequenceStart))
        {
            anchor = sequenceStart.Anchor.IsEmpty ? null : sequenceStart.Anchor.Value;
            var sequence = new Sequence();
            if (anchor is not null)
                anchors[anchor] = sequence;
            while (!parser.TryConsume<SequenceEnd>(out _))
                sequence.Items.Add(ReadNode(parser, anchors, ref read, depth + 1));
            node = sequence;
        }
        else
        {
            var mappingStart = parser.Consume<MappingStart>();
            anchor = mappingStart.Anchor.IsEmpty ? null : mappingStart.Anchor.Value;
            var mapping = new Mapping();
            if (anchor is not null)
                anchors[anchor] = mapping;
            var merged = new List<Mapping>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                bool isMerge = parser.Accept<YamlDotNet.Core.Events.Scalar>(out var keyEvent)
                    && keyEvent.Value == "<<" && keyEvent.Style == ScalarStyle.Plain;
                Node key = ReadNode(parser, anchors, ref read, depth + 1);
                Node value = ReadNode(parser, anchors, ref read, depth + 1);
                if (isMerge && value is Mapping one)
                    merged.Add(one);
                else if (isMerge && value is Sequence many && many.Items.All(i => i is Mapping))
                    merged.AddRange(many.Items.Cast<Mapping>());
                else
                    mapping.Entries.Add((KeyText(key), value));
            }
            // Merged keys go after the mapping's own, and the first mapping to set a key wins, as YAML's merge key says.
            var seen = new HashSet<string>(mapping.Entries.Select(e => e.Key), StringComparer.Ordinal);
            foreach (Mapping source in merged)
            {
                foreach ((string key, Node value) in source.Entries)
                {
                    if (seen.Add(key))
                        mapping.Entries.Add((key, value));
                }
            }
            node = mapping;
        }
        if (anchor is not null)
            anchors[anchor] = node;
        return node;
    }

    private static Scalar ToScalar(YamlDotNet.Core.Events.Scalar scalar)
    {
        if (scalar.Style != ScalarStyle.Plain || !scalar.Tag.IsEmpty && scalar.Tag.Value is "tag:yaml.org,2002:str" or "!")
            return new Scalar(scalar.Value, true);
        return scalar.Value switch
        {
            "" or "~" or "null" or "Null" or "NULL" => new Scalar(null, true),
            "true" or "True" or "TRUE" => new Scalar("true", false),
            "false" or "False" or "FALSE" => new Scalar("false", false),
            _ => new Scalar(scalar.Value, true),
        };
    }

    private static string KeyText(Node key) => key switch
    {
        Scalar { Value: null } => "null",
        Scalar s => s.Value!,
        _ => "?",
    };

    private static void Write(Utf8JsonWriter writer, Node node, ref int written, int depth)
    {
        if (++written > MaxValues)
            throw new InvalidDataException($"The YAML expands to more than {MaxValues} values.");
        if (depth > MaxDepth)
            throw new InvalidDataException($"The YAML nests deeper than {MaxDepth} levels.");
        switch (node)
        {
            case Scalar { Value: null }:
                writer.WriteNullValue();
                break;
            case Scalar { IsText: false } flag:
                writer.WriteBooleanValue(flag.Value == "true");
                break;
            case Scalar text:
                writer.WriteStringValue(text.Value);
                break;
            case Sequence sequence:
                writer.WriteStartArray();
                foreach (Node item in sequence.Items)
                    Write(writer, item, ref written, depth + 1);
                writer.WriteEndArray();
                break;
            case Mapping mapping:
                writer.WriteStartObject();
                foreach ((string key, Node value) in mapping.Entries)
                {
                    writer.WritePropertyName(key);
                    Write(writer, value, ref written, depth + 1);
                }
                writer.WriteEndObject();
                break;
        }
    }
}
