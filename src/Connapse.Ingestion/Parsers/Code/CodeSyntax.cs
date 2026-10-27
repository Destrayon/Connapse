using System.Text.RegularExpressions;
using TreeSitter;

namespace Connapse.Ingestion.Parsers.Code;

/// <summary>
/// Reads a <see cref="CodeOutline"/> from source text with tree-sitter (CON-36). Syntax only: nothing is compiled, run
/// or resolved here. The tree is walked with a cursor and an explicit stack, never by recursion, so a deeply nested
/// file costs memory, not the thread's stack.
/// </summary>
internal static partial class CodeSyntax
{
    private const int MaxSignature = 300;
    private const int MaxReceiver = 200;

    public static CodeOutline Read(CodeGrammar grammar, string source)
    {
        Parser parser = grammar.RentParser();
        using Tree tree = parser.Parse(source) ?? throw new InvalidOperationException("tree-sitter returned no tree.");
        var walk = new CodeWalk(grammar, source);
        Node root = tree.RootNode;
        using var cursor = new TreeCursor(root);
        while (true)
        {
            walk.Enter(cursor.CurrentNode);
            if (cursor.GotoFirstChild())
                continue;
            while (!cursor.GotoNextSibling())
            {
                if (!cursor.GotoParent())
                    return walk.Finish(root.HasError);
            }
        }
    }

    /// <summary>The name a type is written with, without generic arguments, nullability, arrays or <c>global::</c>.</summary>
    public static string TypeName(string text)
    {
        string name = WhitespacePattern().Replace(text, "");
        if (name.StartsWith("global::", StringComparison.Ordinal))
            name = name["global::".Length..];
        if (name.StartsWith("Optional[", StringComparison.Ordinal) && name.EndsWith(']'))
            name = name["Optional[".Length..^1];
        int cut = name.IndexOfAny(['<', '[', '?', '(', '|']);
        return cut >= 0 ? name[..cut] : name;
    }

    /// <summary>A receiver as written when it is a name or a dotted chain of names, else "?".</summary>
    public static string Receiver(Node node)
    {
        string text = WhitespacePattern().Replace(node.Text, "");
        return text.Length <= MaxReceiver && DottedNamePattern().IsMatch(text) ? text : "?";
    }

    public static string Signature(string source, Node node, Node? body)
    {
        int end = body is not null && body.StartIndex > node.StartIndex ? body.StartIndex : node.EndIndex;
        string text = WhitespacePattern().Replace(source[node.StartIndex..end], " ").Trim();
        return text.Length > MaxSignature ? text[..MaxSignature] + "…" : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^[\p{L}_$][\p{L}\p{N}_$]*(\.[\p{L}_$][\p{L}\p{N}_$]*)*$")]
    private static partial Regex DottedNamePattern();
}

/// <summary>The state of one walk: the symbols found so far and which namespace and symbol the cursor is inside.</summary>
internal sealed class CodeWalk(CodeGrammar grammar, string source)
{
    private sealed class SymbolBuilder
    {
        public required int Parent;
        public required string Kind;
        public required string Name;
        public required string Container;
        public required int StartLine;
        public required int EndLine;
        public int ElideStart;
        public int ElideEnd;
        public string Signature = "";
        public readonly List<string> Bases = [];
        public readonly List<CodeCall> Calls = [];
        public readonly List<CodeVariable> Variables = [];
    }

    // What the cursor is inside: a namespace (Symbol -1) or a symbol, until the character index End.
    private readonly List<(int End, string? Namespace, int Symbol)> _scopes = [];
    private readonly List<SymbolBuilder> _symbols = [];
    private readonly List<CodeImport> _imports = [];
    private readonly List<CodeCall> _moduleCalls = [];
    private readonly List<CodeVariable> _moduleVariables = [];

    public string Source => source;

    /// <summary>The innermost symbol the cursor is inside, or -1.</summary>
    public int Current
    {
        get
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].Symbol >= 0)
                    return _scopes[i].Symbol;
            return -1;
        }
    }

    public bool InsideType => Current < 0 || IsType(_symbols[Current].Kind);

    public string? CurrentKind => Current < 0 ? null : _symbols[Current].Kind;

    public void Enter(Node node)
    {
        int start = node.StartIndex;
        while (_scopes.Count > 0 && start >= _scopes[^1].End)
            _scopes.RemoveAt(_scopes.Count - 1);

        if (grammar.NamespaceOf(node) is { } ns)
        {
            // A file-scoped namespace runs to the end of the file, not of its own node.
            int end = node.Type == "file_scoped_namespace_declaration" ? int.MaxValue : node.EndIndex;
            _scopes.Add((end, ns, -1));
        }
        else if (InsideType && grammar.SymbolOf(node, this) is { Name: { } nameNode } match)
        {
            AddSymbol(node, match.Kind, nameNode.Text, match.Span, match.Body);
        }

        grammar.Visit(node, this);
    }

    private void AddSymbol(Node node, string kind, string name, Node spanNode, Node? body)
    {
        int parent = Current;
        string container = parent >= 0
            ? Qualified(_symbols[parent])
            : string.Join('.', _scopes.Where(s => s.Namespace is not null).Select(s => s.Namespace));

        var symbol = new SymbolBuilder
        {
            Parent = parent,
            Kind = kind,
            Name = name,
            Container = container,
            StartLine = LeadingStartRow(spanNode) + 1,
            EndLine = spanNode.EndPosition.Row + 1,
            Signature = CodeSyntax.Signature(source, node, body),
        };
        if (body is not null && grammar.ElidesBody(body))
        {
            (int first, int last) = grammar.BraceBodies
                ? (body.StartPosition.Row + 2, body.EndPosition.Row)
                : (body.StartPosition.Row + 1, body.EndPosition.Row + 1);
            if (last >= first)
                (symbol.ElideStart, symbol.ElideEnd) = (first, last);
        }

        _symbols.Add(symbol);
        _scopes.Add((node.EndIndex, null, _symbols.Count - 1));
    }

    /// <summary>The first row of a definition with the doc comments and decorators directly above it.</summary>
    private static int LeadingStartRow(Node spanNode)
    {
        int row = spanNode.StartPosition.Row;
        for (Node? previous = spanNode.PreviousSibling;
             previous is not null && previous.Type is "comment" or "decorator" && previous.EndPosition.Row >= row - 1;
             previous = previous.PreviousSibling)
        {
            row = previous.StartPosition.Row;
        }
        return row;
    }

    public void AddImport(CodeImport import) => _imports.Add(import);

    public void AddCall(string name, string? receiver, Node at, bool isNew = false)
    {
        if (name.Length == 0)
            return;
        var call = new CodeCall(name, receiver, at.StartPosition.Row + 1, isNew);
        (Current >= 0 ? _symbols[Current].Calls : _moduleCalls).Add(call);
    }

    public void AddVariable(string name, string type)
    {
        string typeName = CodeSyntax.TypeName(type);
        if (name.Length == 0 || typeName.Length == 0 || typeName is "var" or "dynamic" or "object" or "any" or "unknown")
            return;
        (Current >= 0 ? _symbols[Current].Variables : _moduleVariables).Add(new CodeVariable(name, typeName));
    }

    /// <summary>A member's declared type, recorded on the type that holds it.</summary>
    public void AddMemberVariable(string name, string type)
    {
        int index = Current;
        while (index >= 0 && !IsType(_symbols[index].Kind))
            index = _symbols[index].Parent;
        string typeName = CodeSyntax.TypeName(type);
        if (index >= 0 && name.Length > 0 && typeName.Length > 0 && typeName != "var")
            _symbols[index].Variables.Add(new CodeVariable(name, typeName));
    }

    public void AddBase(Node type)
    {
        string name = CodeSyntax.TypeName(type.Text);
        if (Current >= 0 && name.Length > 0)
            _symbols[Current].Bases.Add(name);
    }

    public CodeOutline Finish(bool hasErrors) => new(
        grammar.Name,
        _imports,
        _symbols.Select(s => new CodeSymbol(s.Parent, s.Kind, s.Name, s.Container, s.StartLine, s.EndLine,
            s.ElideStart, s.ElideEnd, s.Signature, s.Bases, s.Calls, s.Variables)).ToList(),
        _moduleCalls,
        _moduleVariables,
        hasErrors);

    private static string Qualified(SymbolBuilder s) => s.Container.Length == 0 ? s.Name : $"{s.Container}.{s.Name}";

    private static bool IsType(string kind) =>
        kind is CodeKinds.Class or CodeKinds.Struct or CodeKinds.Record or CodeKinds.Interface or CodeKinds.EnumType;
}
