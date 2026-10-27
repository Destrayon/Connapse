using System.Globalization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Parsers.Code;

namespace Connapse.Ingestion.Chunking;

/// <summary>
/// Cuts a code file into one passage per definition (CON-108), from the <see cref="CodeOutline"/> its parser sent:
/// <list type="bullet">
/// <item>a function, method, constructor or property is one passage, with its doc comment, attributes and decorators;</item>
/// <item>a type that fits the budget is one passage, whole; a larger one gets an outline passage (its text with each
/// member's body left out) and its members get their own;</item>
/// <item>small neighbouring members of one type share a passage, up to a quarter of the budget each (cAST, arXiv
/// 2506.15655: merging small siblings beat splitting alone by a wide margin);</item>
/// <item>imports and top-level code outside any definition make one passage;</item>
/// <item>a passage over the budget is split at line boundaries.</item>
/// </list>
/// Every passage starts with a line naming what it is, such as <c>method Acme.Billing.Invoice.Pay</c>, so a member read
/// alone still says which type and namespace it belongs to. A file without an outline is split at line boundaries.
/// </summary>
public sealed class CodeChunker(ITokenCounter tokenCounter) : IChunkingStrategy
{
    public const string StrategyName = "Code";

    public const string MetadataKeySymbol = "CodeSymbol";
    public const string MetadataKeyKind = "CodeKind";
    public const string MetadataKeyStartLine = "StartLine";
    public const string MetadataKeyEndLine = "EndLine";

    /// <summary>Tokens kept back for a header's "(part 2 of 3)".</summary>
    private const int PartSuffixTokens = 8;

    private const string Elided = "…";

    public string Name => StrategyName;

    private sealed record Passage(string Kind, string Name, int StartLine, int EndLine, string Body, bool Contiguous);

    public Task<IReadOnlyList<ChunkInfo>> ChunkAsync(
        ParsedDocument parsedDocument, ChunkingSettings settings, CancellationToken cancellationToken = default)
    {
        string content = parsedDocument.Content;
        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult<IReadOnlyList<ChunkInfo>>([]);

        var lines = new SourceLines(content);
        CodeOutline? outline = CodeOutline.From(parsedDocument.Metadata);
        int budget = Math.Max(16, settings.MaxChunkSize);

        IEnumerable<Passage> passages = outline is null
            ? [new Passage("file", "", 1, lines.Count, lines.Text(1, lines.Count), Contiguous: true)]
            : Passages(outline, lines, budget);

        var metadata = new Dictionary<string, string>(parsedDocument.Metadata);
        metadata.Remove(CodeOutline.ParsedDocumentMetadataKey);

        var chunks = new List<ChunkInfo>();
        foreach (Passage passage in passages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Emit(passage, lines, budget, metadata, chunks);
        }
        return Task.FromResult<IReadOnlyList<ChunkInfo>>(chunks);
    }

    private List<Passage> Passages(CodeOutline outline, SourceLines lines, int budget)
    {
        IReadOnlyList<CodeSymbol> symbols = outline.Symbols;
        var passages = new List<Passage>();
        var absorbed = new bool[symbols.Count];

        for (int i = 0; i < symbols.Count; i++)
        {
            if (absorbed[i] || (symbols[i].Parent >= 0 && absorbed[symbols[i].Parent]))
            {
                absorbed[i] = true;
                continue;
            }

            CodeSymbol symbol = symbols[i];
            string whole = lines.Text(symbol.StartLine, symbol.EndLine);
            if (!symbol.IsType)
            {
                passages.Add(new Passage(symbol.Kind, symbol.QualifiedName, symbol.StartLine, symbol.EndLine, whole, Contiguous: true));
                continue;
            }

            if (tokenCounter.CountTokens(Header(symbol.Kind, symbol.QualifiedName) + whole) <= budget)
            {
                // A type that fits is read whole, and its members are not repeated.
                passages.Add(new Passage(symbol.Kind, symbol.QualifiedName, symbol.StartLine, symbol.EndLine, whole, Contiguous: true));
                for (int j = i + 1; j < symbols.Count; j++)
                    if (IsInside(symbols, j, i))
                        absorbed[j] = true;
                continue;
            }

            var elisions = Enumerable.Range(0, symbols.Count)
                .Where(j => IsInside(symbols, j, i) && symbols[j].ElideStart > 0)
                .Select(j => (symbols[j].ElideStart, symbols[j].ElideEnd));
            passages.Add(new Passage(symbol.Kind, symbol.QualifiedName, symbol.StartLine, symbol.EndLine,
                lines.Text(symbol.StartLine, symbol.EndLine, elisions), Contiguous: false));
        }

        passages = MergeSmallMembers(passages, symbols, budget);
        if (ModulePassage(symbols, lines) is { } module)
            passages.Add(module);
        return [.. passages.OrderBy(p => p.StartLine)];
    }

    /// <summary>Whether symbol <paramref name="inner"/> sits anywhere inside symbol <paramref name="outer"/>.</summary>
    private static bool IsInside(IReadOnlyList<CodeSymbol> symbols, int inner, int outer)
    {
        for (int parent = symbols[inner].Parent; parent >= 0; parent = symbols[parent].Parent)
            if (parent == outer)
                return true;
        return false;
    }

    /// <summary>Joins runs of neighbouring small members of one type into a passage, up to the budget.</summary>
    private List<Passage> MergeSmallMembers(List<Passage> passages, IReadOnlyList<CodeSymbol> symbols, int budget)
    {
        var containerOf = symbols.Where(s => !s.IsType).GroupBy(s => s.QualifiedName).ToDictionary(g => g.Key, g => g.First().Container);
        int small = budget / 4;
        var merged = new List<Passage>();
        var run = new List<Passage>();

        void Flush()
        {
            if (run.Count == 1)
                merged.Add(run[0]);
            else if (run.Count > 1)
            {
                string container = containerOf[run[0].Name];
                merged.Add(new Passage("members", container + ": " + string.Join(", ", run.Select(p => p.Name[(container.Length + 1)..])),
                    run[0].StartLine, run[^1].EndLine, string.Join("\n", run.Select(p => p.Body)), Contiguous: false));
            }
            run.Clear();
        }

        foreach (Passage passage in passages.OrderBy(p => p.StartLine))
        {
            bool isSmallMember = containerOf.TryGetValue(passage.Name, out string? container) && container.Length > 0
                && symbols.Any(s => s.IsType && s.QualifiedName == container)
                && tokenCounter.CountTokens(passage.Body) <= small;
            if (isSmallMember && run.Count > 0
                && containerOf[run[0].Name] == container
                && tokenCounter.CountTokens(string.Join("\n", run.Select(p => p.Body)) + "\n" + passage.Body) <= budget - 32)
            {
                run.Add(passage);
                continue;
            }

            Flush();
            if (isSmallMember)
                run.Add(passage);
            else
                merged.Add(passage);
        }
        Flush();
        return merged;
    }

    /// <summary>Everything outside the top-level definitions: imports, namespace lines, top-level statements.</summary>
    private static Passage? ModulePassage(IReadOnlyList<CodeSymbol> symbols, SourceLines lines)
    {
        var covered = symbols.Where(s => s.Parent < 0).Select(s => (s.StartLine, s.EndLine)).ToList();
        string text = lines.Text(1, lines.Count, covered);
        bool meaningful = text.Split('\n').Any(l => l.Trim() is { Length: > 0 } t && t != Elided && t.Any(char.IsLetterOrDigit));
        if (!meaningful)
            return null;
        int first = Enumerable.Range(1, lines.Count).FirstOrDefault(n => !covered.Any(c => n >= c.StartLine && n <= c.EndLine) && lines[n].Trim().Length > 0, 1);
        return new Passage("module", "", first, lines.Count, text, Contiguous: false);
    }

    private static string Header(string kind, string name) => name.Length == 0 ? kind + "\n" : $"{kind} {name}\n";

    private void Emit(Passage passage, SourceLines lines, int budget, Dictionary<string, string> metadata, List<ChunkInfo> chunks)
    {
        string header = passage.Kind == "file" ? "" : Header(passage.Kind, passage.Name);
        int room = budget - tokenCounter.CountTokens(header) - PartSuffixTokens;
        List<string> parts = tokenCounter.CountTokens(header + passage.Body) <= budget
            ? [passage.Body]
            : SplitLines(passage.Body, Math.Max(8, room));

        for (int part = 0; part < parts.Count; part++)
        {
            string partHeader = parts.Count == 1 || header.Length == 0
                ? header
                : $"{header.TrimEnd('\n')} (part {part + 1} of {parts.Count})\n";
            string text = (partHeader + parts[part]).Trim();
            int index = chunks.Count;
            var chunkMetadata = new Dictionary<string, string>(metadata)
            {
                ["ChunkingStrategy"] = StrategyName,
                ["ChunkIndex"] = index.ToString(CultureInfo.InvariantCulture),
                [MetadataKeyKind] = passage.Kind,
                [MetadataKeyStartLine] = passage.StartLine.ToString(CultureInfo.InvariantCulture),
                [MetadataKeyEndLine] = passage.EndLine.ToString(CultureInfo.InvariantCulture),
            };
            if (passage.Name.Length > 0)
                chunkMetadata[MetadataKeySymbol] = passage.Name;
            if (!passage.Contiguous || parts.Count > 1)
                chunkMetadata["OffsetEstimated"] = "true";

            chunks.Add(new ChunkInfo(text, index, tokenCounter.CountTokens(text),
                lines.Offset(passage.StartLine), lines.EndOffset(passage.EndLine), chunkMetadata));
        }
    }

    /// <summary>Packs whole lines into pieces of at most <paramref name="room"/> tokens; a longer line is cut.</summary>
    private List<string> SplitLines(string body, int room)
    {
        var parts = new List<string>();
        var current = new List<string>();
        int currentTokens = 0;
        foreach (string line in body.Split('\n'))
        {
            int tokens = tokenCounter.CountTokens(line) + 1;
            if (current.Count > 0 && currentTokens + tokens > room)
            {
                parts.Add(string.Join("\n", current));
                current.Clear();
                currentTokens = 0;
            }

            string rest = line;
            while (tokenCounter.CountTokens(rest) > room)
            {
                int cut = Math.Max(1, tokenCounter.GetIndexAtTokenCount(rest, room));
                parts.Add(rest[..cut]);
                rest = rest[cut..];
            }
            current.Add(rest);
            currentTokens += tokenCounter.CountTokens(rest) + 1;
        }
        if (current.Any(l => l.Trim().Length > 0))
            parts.Add(string.Join("\n", current));
        return parts;
    }

    /// <summary>A file's lines, 1-based, and where each starts in the text.</summary>
    private sealed class SourceLines
    {
        private readonly string[] _lines;
        private readonly int[] _starts;
        private readonly int _length;

        public SourceLines(string content)
        {
            _lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            _starts = new int[_lines.Length + 1];
            int offset = 0;
            string[] raw = content.Split('\n');
            for (int i = 0; i < raw.Length; i++)
            {
                _starts[i] = offset;
                offset += raw[i].Length + 1;
            }
            _starts[raw.Length] = content.Length;
            _length = content.Length;
        }

        public int Count => _lines.Length;

        public string this[int line] => _lines[line - 1];

        public int Offset(int line) => _starts[Math.Clamp(line, 1, Count) - 1];

        public int EndOffset(int line) => Math.Min(_length, _starts[Math.Clamp(line, 1, Count)]);

        /// <summary>Lines <paramref name="first"/> to <paramref name="last"/>, each elided range replaced by one "…" line.</summary>
        public string Text(int first, int last, IEnumerable<(int Start, int End)>? elisions = null)
        {
            first = Math.Clamp(first, 1, Count);
            last = Math.Clamp(last, first, Count);
            var ranges = (elisions ?? []).Where(e => e.End >= e.Start).OrderBy(e => e.Start).ToList();
            var text = new List<string>();
            for (int line = first; line <= last; line++)
            {
                int index = ranges.FindIndex(r => line >= r.Start && line <= r.End);
                if (index < 0)
                {
                    text.Add(this[line]);
                    continue;
                }

                string indent = new([.. this[line].TakeWhile(char.IsWhiteSpace)]);
                text.Add(indent + Elided);
                line = ranges.Where(r => r.Start <= line && r.End >= line).Max(r => r.End);
            }
            return string.Join("\n", text);
        }
    }
}
