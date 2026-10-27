using Connapse.Core;

namespace Connapse.Ingestion.Parsers.Code;

/// <summary>
/// Turns one file's <see cref="CodeOutline"/> into link-layer nodes and links (CON-109): the file
/// <see cref="Defines"/> its types and functions and a type its members; the file <see cref="Imports"/> namespaces and
/// modules; a definition <see cref="Calls"/> what it calls or constructs; a type <see cref="Inherits"/> its bases.
/// <para>
/// Everything is decided from this one file, so a sync re-reads only the files that changed. That works because node
/// ids are names: a call to <c>Acme.Billing.Ledger.Post</c> points at the node every file defining that method
/// would name, whether or not one has been indexed yet. When a name could mean several things, each candidate gets a
/// link, and the link layer shows only links whose both ends some readable document names, so a guess at a node no
/// file defines stays invisible.
/// </para>
/// <para>
/// Confidence, by how the target was found (no published tool scores these; the ladder is ours, after
/// code-graph-rag's exact/heuristic tiers and Aider's down-weighting of common names):
/// <list type="bullet">
/// <item>1.0: what the file states: a definition, a <c>using</c> or a relative import;</item>
/// <item>0.95: a definition in the same file;</item>
/// <item>0.9: a name imported by name (<c>from m import f</c>, <c>import { f } from './m'</c>), or a receiver whose
/// declared type is defined in this file;</item>
/// <item>0.85: a module imported whole (<c>osp.join</c> after <c>import os.path as osp</c>);</item>
/// <item>0.7: a type found through a namespace in scope or an import, or a receiver whose declared type was found so;</item>
/// <item>0.5: a member of this type or a base that this file does not show, which may be inherited or partial;</item>
/// <item>0.4: a name from a <c>from m import *</c>.</item>
/// </list>
/// A call whose receiver's type cannot be told from the file gets no link.
/// </para>
/// </summary>
public static class CodeLinks
{
    public const string Defines = "defines";
    public const string Imports = "imports";
    public const string Calls = "calls";
    public const string Inherits = "inherits";

    /// <summary>The most links one call or base may produce; more candidates than this are not worth a guess.</summary>
    private const int MaxCandidates = 6;

    private const int MaxEvidence = 200;

    public static DocumentLinks Build(string documentId, OwnerRef owner, string path, string content, CodeOutline outline)
    {
        var builder = new Builder(documentId, owner, path.Replace('\\', '/').TrimStart('/'), content, outline);
        return builder.Build();
    }

    private sealed record Target(string Key, double Confidence, string How);

    private sealed class Builder(string documentId, OwnerRef owner, string path, string content, CodeOutline outline)
    {
        private readonly string _owner = (owner.IsSource ? "s" : "c") + owner.Id.ToString("N");
        private readonly string[] _lines = content.Split('\n');
        private readonly List<LinkFact> _facts = [];
        private readonly Dictionary<string, LinkAssertion> _links = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<CodeSymbol> _symbols = outline.Symbols;
        private readonly string _module = ModuleOf(outline.Language, path.Replace('\\', '/').TrimStart('/'));

        // C#: usings and aliases. Python and TypeScript: names bound by imports, modules imported whole, wildcards.
        private readonly List<string> _usings = [];
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _importedNames = new(StringComparer.Ordinal);
        private readonly List<string> _wildcards = [];

        private string Lang => outline.Language;

        public DocumentLinks Build()
        {
            string fileNode = FileNode();
            _facts.Add(new LinkFact(fileNode, LinkFactField.Kind, "file"));
            _facts.Add(new LinkFact(fileNode, LinkFactField.Name, path));

            ReadImports(fileNode);

            var symbolNodes = new List<string>();
            for (int i = 0; i < _symbols.Count; i++)
            {
                CodeSymbol symbol = _symbols[i];
                string node = Node(SymbolKey(symbol));
                symbolNodes.Add(node);
                _facts.Add(new LinkFact(node, LinkFactField.Kind, symbol.Kind));
                _facts.Add(new LinkFact(node, LinkFactField.Name, DisplayName(symbol)));
                if (symbol.Signature.Length > 0)
                    _facts.Add(new LinkFact(node, LinkFactField.Summary, symbol.Signature));

                string parent = symbol.Parent >= 0 ? Node(SymbolKey(_symbols[symbol.Parent])) : NamespaceNode(symbol) ?? fileNode;
                Add(Defines, parent, node, symbol.StartLine, 1.0, "stated");

                foreach (string baseName in symbol.Bases)
                    foreach (Target target in ResolveType(baseName, i))
                        Add(Inherits, node, Node(target.Key), symbol.StartLine, target.Confidence, target.How);

                foreach (CodeCall call in symbol.Calls)
                    foreach (Target target in ResolveCall(call, i))
                        Add(Calls, node, Node(target.Key), call.Line, target.Confidence, target.How);
            }

            foreach (CodeCall call in outline.ModuleCalls)
                foreach (Target target in ResolveCall(call, -1))
                    Add(Calls, fileNode, Node(target.Key), call.Line, target.Confidence, target.How);

            return new DocumentLinks(documentId, fileNode, _facts, [.. _links.Values], [.. symbolNodes.Distinct(StringComparer.Ordinal)]);
        }

        // ---- nodes -------------------------------------------------------------------------------------------

        private string Node(string key) => $"code:{_owner}:{Lang}:{key}";

        /// <summary>Python and TypeScript files are their module's node, which imports point at; a C# file is its path.</summary>
        private string FileNode() => Lang == "csharp" ? $"code:{_owner}:file:{path}" : Node(_module);

        private string SymbolKey(CodeSymbol symbol) => Lang switch
        {
            "csharp" => symbol.QualifiedName,
            "python" => $"{_module}.{symbol.QualifiedName}",
            _ => $"{_module}#{symbol.QualifiedName}",
        };

        private string DisplayName(CodeSymbol symbol) => Lang == "typescript" ? $"{symbol.QualifiedName} ({path})" : SymbolKey(symbol);

        /// <summary>A C# top-level type sits in its namespace's node, which the file defines.</summary>
        private string? NamespaceNode(CodeSymbol symbol)
        {
            if (Lang != "csharp" || symbol.Container.Length == 0)
                return null;
            string node = $"code:{_owner}:csharp-namespace:{symbol.Container}";
            if (!_facts.Any(f => f.NodeId == node))
            {
                _facts.Add(new LinkFact(node, LinkFactField.Kind, "namespace"));
                _facts.Add(new LinkFact(node, LinkFactField.Name, symbol.Container));
                Add(Defines, FileNode(), node, symbol.StartLine, 1.0, "stated");
            }
            return node;
        }

        internal static string ModuleOf(string language, string path)
        {
            string stem = path;
            foreach (string extension in new[] { ".d.ts", ".tsx", ".ts", ".mts", ".cts", ".pyi", ".py", ".cs" })
            {
                if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^extension.Length];
                    break;
                }
            }

            if (language == "python")
            {
                var parts = stem.Split('/').ToList();
                while (parts.Count > 1 && parts[0] is "src" or "lib")
                    parts.RemoveAt(0);
                if (parts.Count > 1 && parts[^1] == "__init__")
                    parts.RemoveAt(parts.Count - 1);
                return string.Join('.', parts);
            }

            return stem.EndsWith("/index", StringComparison.Ordinal) ? stem[..^"/index".Length] : stem;
        }

        // ---- imports -----------------------------------------------------------------------------------------

        private void ReadImports(string fileNode)
        {
            foreach (CodeImport import in outline.Imports)
            {
                switch (Lang)
                {
                    case "csharp":
                        if (import.Alias is { } usingAlias)
                            _aliases[usingAlias] = import.Target;
                        else
                        {
                            _usings.Add(import.Target);
                            Add(Imports, fileNode, $"code:{_owner}:csharp-namespace:{import.Target}", import.Line, 1.0, "stated");
                        }
                        break;

                    case "python":
                        {
                            string module = ResolvePythonModule(import.Target);
                            double confidence = import.Target.StartsWith('.') ? 1.0 : 0.9;
                            Add(Imports, fileNode, Node(module), import.Line, confidence, import.Target.StartsWith('.') ? "relative" : "module path");
                            // import a.b binds "a", which ResolveCall reads from the imports; import a.b as ab binds "ab".
                            if (import.Names.Count == 0 && import.Alias is { } pythonAlias)
                                _aliases[pythonAlias] = module;
                            foreach (string name in import.Names)
                            {
                                if (name == "*")
                                    _wildcards.Add(module);
                                else
                                    BindName(name, n => $"{module}.{n}");
                            }
                            break;
                        }

                    default:
                        {
                            // Only relative imports name a file in the same repository; packages are outside it.
                            if (!import.Target.StartsWith('.'))
                                break;
                            string module = ModuleOf(Lang, Join(Directory(path), import.Target));
                            Add(Imports, fileNode, Node(module), import.Line, 1.0, "relative");
                            if (import.Alias is { } moduleAlias)
                                _aliases[moduleAlias] = module;
                            foreach (string name in import.Names.Where(n => !n.StartsWith("default as ", StringComparison.Ordinal)))
                                BindName(name, n => $"{module}#{n}");
                            break;
                        }
                }
            }
        }

        private void BindName(string name, Func<string, string> key)
        {
            int at = name.IndexOf(" as ", StringComparison.Ordinal);
            string imported = at < 0 ? name : name[..at];
            string local = at < 0 ? name : name[(at + 4)..];
            _importedNames[local] = key(imported);
        }

        /// <summary>A Python module as written in an import, resolved against this file's module when relative.</summary>
        private string ResolvePythonModule(string target)
        {
            int dots = target.TakeWhile(c => c == '.').Count();
            if (dots == 0)
                return target;
            var package = _module.Split('.').ToList();
            // From a package's __init__ the module is the package itself, so one dot means it, not its parent.
            bool isPackage = path.EndsWith("__init__.py", StringComparison.Ordinal) || path.EndsWith("__init__.pyi", StringComparison.Ordinal);
            int up = isPackage ? dots - 1 : dots;
            package = package.Take(Math.Max(0, package.Count - up)).ToList();
            string rest = target[dots..];
            if (rest.Length > 0)
                package.Add(rest);
            return string.Join('.', package);
        }

        private static string Directory(string file)
        {
            int slash = file.LastIndexOf('/');
            return slash < 0 ? "" : file[..slash];
        }

        private static string Join(string directory, string relative)
        {
            var parts = directory.Length == 0 ? [] : directory.Split('/').ToList();
            foreach (string part in relative.Split('/'))
            {
                if (part is "." or "")
                    continue;
                if (part == "..")
                {
                    if (parts.Count > 0)
                        parts.RemoveAt(parts.Count - 1);
                }
                else
                    parts.Add(part);
            }
            return string.Join('/', parts);
        }

        // ---- resolution --------------------------------------------------------------------------------------

        /// <summary>The types a name may mean, as seen from symbol <paramref name="from"/> (-1 for the file).</summary>
        private List<Target> ResolveType(string name, int from)
        {
            string first = name.Split('.')[0];
            string rest = name[first.Length..];

            if (_aliases.TryGetValue(first, out string? aliased))
                return [new Target(Lang == "typescript" && rest.Length > 0 ? aliased + "#" + rest[1..] : aliased + rest, 0.85, "import alias")];

            var local = _symbols.Select((s, i) => (s, i)).Where(x => x.s.IsType && x.s.Name == name).ToList();
            if (local.Count > 0)
                return Limit(local.Select(x => new Target(SymbolKey(x.s), local.Count == 1 ? 0.95 : 0.5, "same file")));

            if (Lang != "csharp" && _importedNames.TryGetValue(first, out string? imported))
                return [new Target(imported + rest, 0.9, "imported name")];

            if (Lang == "csharp")
            {
                var candidates = new List<string>();
                string ns = from >= 0 ? NamespaceOf(from) : "";
                for (string scope = ns; scope.Length > 0; scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : "")
                    candidates.Add($"{scope}.{name}");
                candidates.AddRange(_usings.Select(u => $"{u}.{name}"));
                if (name.Contains('.'))
                    candidates.Add(name);
                return Limit(candidates.Distinct(StringComparer.Ordinal).Select(c => new Target(c, 0.7, "namespace in scope")));
            }

            return Limit(_wildcards.Select(m => new Target($"{m}.{name}", 0.4, "wildcard import")));
        }

        private List<Target> ResolveCall(CodeCall call, int from)
        {
            if (call.IsNew)
                return ResolveType(call.Receiver is null ? call.Name : $"{call.Receiver}.{call.Name}", from);

            int type = EnclosingType(from);
            string? receiver = call.Receiver;
            if (receiver is not null && (receiver.StartsWith("this.", StringComparison.Ordinal) || receiver.StartsWith("self.", StringComparison.Ordinal)))
                return Member(TypesOfVariable(receiver[5..], from), call.Name, 0.7);

            switch (receiver)
            {
                case null when Lang != "csharp":
                    // A bare name in Python or TypeScript, even inside a method, is a module-level function or class.
                    return ResolveFreeName(call.Name);
                case null or "this" or "self" or "cls":
                    return type < 0 ? [] : OwnMember(type, call.Name);
                case "base" or "super":
                    return type < 0 ? [] : BaseMembers(type, call.Name, 0.6);
                case "?":
                    return [];
            }

            string first = receiver.Split('.')[0];
            if (!receiver.Contains('.') && TypesOfVariable(receiver, from) is { Count: > 0 } typed)
                return Member(typed, call.Name, 0.7);
            if (_aliases.TryGetValue(first, out string? module))
            {
                string key = module + receiver[first.Length..];
                return [new Target(Lang == "typescript" ? $"{key}#{call.Name}" : $"{key}.{call.Name}", 0.85, "import alias")];
            }
            if (Lang == "python" && outline.Imports.Any(i => i.Alias is null && i.Names.Count == 0 && (i.Target == receiver || i.Target.StartsWith(first + ".", StringComparison.Ordinal) || i.Target == first)))
                return [new Target($"{receiver}.{call.Name}", 0.85, "module import")];
            if (char.IsUpper(first[0]))
                return Member(ResolveType(receiver, from), call.Name, 0.7);
            return [];
        }

        /// <summary>A bare call in Python or TypeScript: a function or class of this module, or one it imports.</summary>
        private List<Target> ResolveFreeName(string name)
        {
            var local = _symbols.Where(s => s.Parent < 0 && s.Name == name).ToList();
            if (local.Count > 0)
                return [new Target(SymbolKey(local[0]), 0.95, "same file")];
            if (_importedNames.TryGetValue(name, out string? imported))
                return [new Target(imported, 0.9, "imported name")];
            return Limit(_wildcards.Select(m => new Target($"{m}.{name}", 0.4, "wildcard import")));
        }

        /// <summary>A member of type <paramref name="type"/>: defined here, else maybe in a partial part or a base.</summary>
        private List<Target> OwnMember(int type, string name)
        {
            string key = SymbolKey(_symbols[type]);
            if (_symbols.Any(s => s.Parent == type && s.Name == name))
                return [new Target($"{key}.{name}", 0.95, "same file")];
            return Limit([new Target($"{key}.{name}", 0.5, "inherited or partial"), .. BaseMembers(type, name, 0.5)]);
        }

        private List<Target> BaseMembers(int type, string name, double cap) =>
            Member(_symbols[type].Bases.SelectMany(b => ResolveType(b, type)).ToList(), name, cap);

        /// <summary>Member <paramref name="name"/> of each type, no surer than <paramref name="cap"/> unless the type is defined here.</summary>
        private List<Target> Member(List<Target> types, string name, double cap)
        {
            var targets = new List<Target>();
            foreach (Target type in types)
            {
                int local = _symbols.Select((s, i) => (s, i)).Where(x => x.s.IsType && SymbolKey(x.s) == type.Key).Select(x => x.i).DefaultIfEmpty(-1).First();
                bool defined = local >= 0 && _symbols.Any(s => s.Parent == local && s.Name == name);
                double confidence = defined ? 0.9 : Math.Min(cap, type.Confidence);
                targets.Add(new Target($"{type.Key}.{name}", confidence, defined ? "declared type, same file" : type.How));
            }
            return Limit(targets);
        }

        /// <summary>The types a variable was declared with, looking outward from symbol <paramref name="from"/>.</summary>
        private List<Target> TypesOfVariable(string name, int from)
        {
            for (int i = from; i >= 0; i = _symbols[i].Parent)
            {
                if (_symbols[i].Variables.FirstOrDefault(v => v.Name == name) is { } variable)
                    return ResolveType(variable.Type, from);
            }
            return outline.ModuleVariables.FirstOrDefault(v => v.Name == name) is { } module ? ResolveType(module.Type, from) : [];
        }

        private int EnclosingType(int from)
        {
            for (int i = from; i >= 0; i = _symbols[i].Parent)
                if (_symbols[i].IsType)
                    return i;
            return -1;
        }

        /// <summary>The namespace a C# symbol sits in: its outermost type's container.</summary>
        private string NamespaceOf(int symbol)
        {
            int top = symbol;
            while (_symbols[top].Parent >= 0)
                top = _symbols[top].Parent;
            return _symbols[top].Container;
        }

        private static List<Target> Limit(IEnumerable<Target> targets) =>
            [.. targets.DistinctBy(t => t.Key).Take(MaxCandidates)];

        // ---- links -------------------------------------------------------------------------------------------

        private void Add(string type, string from, string to, int line, double confidence, string how)
        {
            if (from == to)
                return;
            string id = $"{type}:{from}->{to}";
            if (_links.TryGetValue(id, out LinkAssertion? existing) && existing.Confidence >= confidence)
                return;
            _links[id] = new LinkAssertion(id, from, to, type, Evidence(line, how), confidence);
        }

        private string Evidence(int line, string how)
        {
            string text = line >= 1 && line <= _lines.Length ? _lines[line - 1].Trim() : "";
            string evidence = $"{path}:{line} ({how}) {text}";
            return evidence.Length > MaxEvidence ? evidence[..MaxEvidence] + "…" : evidence;
        }
    }
}
