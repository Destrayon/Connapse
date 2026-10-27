using TreeSitter;
using static Connapse.Ingestion.Parsers.Code.CodeKinds;

namespace Connapse.Ingestion.Parsers.Code;

/// <summary>A definition found at a node: its kind, its name, the node its lines span, and its body.</summary>
internal readonly record struct SymbolMatch(string Kind, Node? Name, Node Span, Node? Body);

/// <summary>
/// What one language's tree-sitter grammar calls its definitions, imports, calls and declared types. Node and field
/// names are the grammar's own (tree-sitter-c-sharp, tree-sitter-typescript, tree-sitter-python).
/// </summary>
internal abstract class CodeGrammar
{
    private readonly Lazy<Language> _language;
    private readonly ThreadLocal<Parser> _parser;

    protected CodeGrammar(string treeSitterId)
    {
        _language = new Lazy<Language>(() => new Language(treeSitterId));
        // A parser holds state between parses, so each thread keeps its own; the language is read-only and shared.
        _parser = new ThreadLocal<Parser>(() => new Parser(_language.Value));
    }

    /// <summary>The outline's language name.</summary>
    public abstract string Name { get; }

    public Parser RentParser() => _parser.Value!;

    /// <summary>Whether bodies are wrapped in braces, whose own lines a type's outline keeps.</summary>
    public virtual bool BraceBodies => true;

    public virtual string? NamespaceOf(Node node) => null;

    public abstract SymbolMatch? SymbolOf(Node node, CodeWalk walk);

    public abstract bool ElidesBody(Node body);

    /// <summary>Records what a node imports, calls, declares or inherits.</summary>
    public abstract void Visit(Node node, CodeWalk walk);

    protected static bool IsTypeKind(string kind) => kind is Class or Struct or Record or Interface or EnumType;

    /// <summary>The node a definition's lines start from: its decorators or <c>export</c> wrapped around it.</summary>
    protected static Node SpanOf(Node node)
    {
        Node span = node;
        while (span.Parent is { Type: "decorated_definition" or "export_statement" or "lexical_declaration" } parent)
            span = parent;
        return span;
    }

    public static readonly CodeGrammar CSharp = new CSharpGrammar();
    public static readonly CodeGrammar TypeScript = new TypeScriptGrammar("typescript");
    public static readonly CodeGrammar Tsx = new TypeScriptGrammar("tsx");
    public static readonly CodeGrammar Python = new PythonGrammar();

    public static CodeGrammar? ForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => CSharp,
        ".ts" or ".mts" or ".cts" => TypeScript,
        ".tsx" => Tsx,
        ".py" or ".pyi" => Python,
        _ => null,
    };
}

internal sealed class CSharpGrammar() : CodeGrammar("c-sharp")
{
    public override string Name => "csharp";

    public override string? NamespaceOf(Node node) =>
        node.Type is "namespace_declaration" or "file_scoped_namespace_declaration"
            ? node.GetChildForField("name") is { } name ? CodeSyntax.TypeName(name.Text) : null
            : null;

    public override SymbolMatch? SymbolOf(Node node, CodeWalk walk)
    {
        string? kind = node.Type switch
        {
            "class_declaration" => Class,
            "struct_declaration" => Struct,
            "record_declaration" or "record_struct_declaration" => Record,
            "interface_declaration" => Interface,
            "enum_declaration" => EnumType,
            "method_declaration" => Method,
            "constructor_declaration" => Constructor,
            "property_declaration" => Property,
            _ => null,
        };
        // Members only inside a type: a local function in top-level statements stays part of the file's text.
        if (kind is null || (!IsTypeKind(kind) && walk.Current < 0))
            return null;
        return new SymbolMatch(kind, node.GetChildForField("name"), node, IsTypeKind(kind) ? null : node.GetChildForField("body"));
    }

    public override bool ElidesBody(Node body) => body.Type == "block";

    public override void Visit(Node node, CodeWalk walk)
    {
        switch (node.Type)
        {
            case "using_directive":
                {
                    IReadOnlyList<Node> named = node.NamedChildren;
                    if (named.Count == 0)
                        break;
                    bool aliased = node.Children.Any(c => c.Type == "=");
                    walk.AddImport(new CodeImport(node.StartPosition.Row + 1, CodeSyntax.TypeName(named[^1].Text), [],
                        aliased && named.Count > 1 ? named[0].Text : null));
                    break;
                }
            case "invocation_expression":
                AddCall(node.GetChildForField("function"), node, walk);
                break;
            case "object_creation_expression" when node.GetChildForField("type") is { } type:
                AddConstruction(CodeSyntax.TypeName(type.Text), node, walk);
                break;
            case "implicit_object_creation_expression" when DeclaredTypeOf(node) is { } declared:
                // new() takes the type it is assigned to: a field, property or local declared with one.
                AddConstruction(CodeSyntax.TypeName(declared), node, walk);
                break;
            case "base_list":
                foreach (Node child in node.NamedChildren)
                {
                    if (child.Type == "argument_list")
                        continue;
                    walk.AddBase(child.Type == "primary_constructor_base_type" && child.FirstNamedChild is { } baseType ? baseType : child);
                }
                break;
            case "parameter" when node.GetChildForField("type") is { } type && node.GetChildForField("name") is { } name:
                walk.AddVariable(name.Text, type.Text);
                break;
            case "variable_declaration" when node.GetChildForField("type") is { } type:
                foreach (Node declarator in node.NamedChildren.Where(c => c.Type == "variable_declarator"))
                {
                    Node? name = declarator.GetChildForField("name") ?? declarator.NamedChildren.FirstOrDefault(c => c.Type == "identifier");
                    string typeText = type.Text == "var" ? ConstructedType(declarator) ?? "var" : type.Text;
                    if (name is not null)
                        walk.AddVariable(name.Text, typeText);
                }
                break;
            case "property_declaration" when node.GetChildForField("type") is { } type && node.GetChildForField("name") is { } name:
                walk.AddMemberVariable(name.Text, type.Text);
                break;
        }
    }

    private static void AddCall(Node? function, Node at, CodeWalk walk)
    {
        switch (function?.Type)
        {
            case "identifier":
                walk.AddCall(function.Text, null, at);
                break;
            case "generic_name":
                walk.AddCall(function.FirstNamedChild?.Text ?? "", null, at);
                break;
            case "member_access_expression":
                {
                    Node? expression = function.GetChildForField("expression");
                    walk.AddCall(SimpleName(function.GetChildForField("name")), expression is null ? "?" : CodeSyntax.Receiver(expression), at);
                    break;
                }
            case "conditional_access_expression"
                when function.NamedChildren.LastOrDefault() is { Type: "member_binding_expression" } binding:
                {
                    // a?.B(): the receiver is the condition.
                    Node? condition = function.GetChildForField("condition");
                    walk.AddCall(SimpleName(binding.GetChildForField("name")), condition is null ? "?" : CodeSyntax.Receiver(condition), at);
                    break;
                }
        }
    }

    private static string SimpleName(Node? name) =>
        name is null ? "" : name.Type == "generic_name" ? name.FirstNamedChild?.Text ?? "" : name.Text;

    internal static void AddConstruction(string typeName, Node at, CodeWalk walk)
    {
        int dot = typeName.LastIndexOf('.');
        walk.AddCall(dot >= 0 ? typeName[(dot + 1)..] : typeName, dot >= 0 ? typeName[..dot] : null, at, isNew: true);
    }

    /// <summary>The type of the declaration a target-typed <c>new()</c> initialises, or null.</summary>
    private static string? DeclaredTypeOf(Node creation)
    {
        Node? parent = creation.Parent;
        if (parent?.Type == "equals_value_clause")
            parent = parent.Parent;
        if (parent?.Type == "variable_declarator")
            parent = parent.Parent;
        string? type = parent?.Type is "variable_declaration" or "property_declaration" ? parent.GetChildForField("type")?.Text : null;
        return type is null or "var" ? null : type;
    }

    /// <summary>The type a <c>var</c> declarator constructs, as in <c>var x = new Foo()</c>.</summary>
    private static string? ConstructedType(Node declarator)
    {
        foreach (Node child in declarator.NamedChildren)
        {
            Node value = child.Type == "equals_value_clause" && child.FirstNamedChild is { } inner ? inner : child;
            if (value.Type == "object_creation_expression" && value.GetChildForField("type") is { } type)
                return type.Text;
        }
        return null;
    }
}

internal sealed class TypeScriptGrammar(string treeSitterId) : CodeGrammar(treeSitterId)
{
    public override string Name => "typescript";

    public override SymbolMatch? SymbolOf(Node node, CodeWalk walk)
    {
        Node? value = node.GetChildForField("value");
        string? kind = node.Type switch
        {
            "class_declaration" or "abstract_class_declaration" => Class,
            "interface_declaration" => Interface,
            "enum_declaration" => EnumType,
            "function_declaration" or "generator_function_declaration" when walk.Current < 0 => Function,
            "method_definition" or "method_signature" or "abstract_method_signature" when walk.Current >= 0 =>
                node.GetChildForField("name")?.Text == "constructor" ? Constructor : Method,
            "variable_declarator" when walk.Current < 0 && IsFunction(value) => Function,
            "public_field_definition" when walk.Current >= 0 && IsFunction(value) => Method,
            _ => null,
        };
        if (kind is null)
            return null;

        Node? body = IsTypeKind(kind) ? null : IsFunction(value) ? value!.GetChildForField("body") : node.GetChildForField("body");
        return new SymbolMatch(kind, node.GetChildForField("name"), SpanOf(node), body);
    }

    private static bool IsFunction(Node? value) =>
        value?.Type is "arrow_function" or "function_expression" or "function" or "generator_function";

    public override bool ElidesBody(Node body) => body.Type == "statement_block";

    public override void Visit(Node node, CodeWalk walk)
    {
        switch (node.Type)
        {
            case "import_statement" when node.GetChildForField("source") is { } source:
                AddImport(node, source.Text.Trim('"', '\'', '`'), walk);
                break;
            case "call_expression":
                AddCall(node.GetChildForField("function"), node, walk, isNew: false);
                break;
            case "new_expression":
                AddCall(node.GetChildForField("constructor"), node, walk, isNew: true);
                break;
            case "extends_clause" or "implements_clause" or "extends_type_clause":
                foreach (Node child in node.NamedChildren.Where(c => c.Type != "type_arguments"))
                    walk.AddBase(child);
                break;
            case "required_parameter" or "optional_parameter"
                when node.GetChildForField("pattern") is { Type: "identifier" } name && AnnotatedType(node) is { } type:
                walk.AddVariable(name.Text, type);
                // constructor(private store: Store) declares a member as well.
                if (node.NamedChildren.Any(c => c.Type == "accessibility_modifier"))
                    walk.AddMemberVariable(name.Text, type);
                break;
            case "variable_declarator" when node.GetChildForField("name") is { Type: "identifier" } name:
                {
                    string? type = AnnotatedType(node);
                    if (type is null && node.GetChildForField("value") is { Type: "new_expression" } created)
                        type = created.GetChildForField("constructor")?.Text;
                    if (type is not null)
                        walk.AddVariable(name.Text, type);
                    break;
                }
            case "public_field_definition" when node.GetChildForField("name") is { } name && AnnotatedType(node) is { } type:
                walk.AddMemberVariable(name.Text, type);
                break;
        }
    }

    private static void AddImport(Node node, string source, CodeWalk walk)
    {
        var names = new List<string>();
        string? alias = null;
        foreach (Node clause in node.NamedChildren.Where(c => c.Type == "import_clause"))
        {
            foreach (Node part in clause.NamedChildren)
            {
                switch (part.Type)
                {
                    case "identifier":
                        names.Add($"default as {part.Text}");
                        break;
                    case "named_imports":
                        foreach (Node specifier in part.NamedChildren.Where(s => s.Type == "import_specifier"))
                        {
                            string name = specifier.GetChildForField("name")?.Text ?? "";
                            string? asName = specifier.GetChildForField("alias")?.Text;
                            names.Add(asName is null ? name : $"{name} as {asName}");
                        }
                        break;
                    case "namespace_import":
                        alias = part.NamedChildren.FirstOrDefault(c => c.Type == "identifier")?.Text;
                        break;
                }
            }
        }
        walk.AddImport(new CodeImport(node.StartPosition.Row + 1, source, names, alias));
    }

    private static void AddCall(Node? function, Node at, CodeWalk walk, bool isNew)
    {
        switch (function?.Type)
        {
            case "identifier":
                walk.AddCall(function.Text, null, at, isNew);
                break;
            case "member_expression":
                {
                    Node? target = function.GetChildForField("object");
                    walk.AddCall(function.GetChildForField("property")?.Text ?? "", target is null ? "?" : CodeSyntax.Receiver(target), at, isNew);
                    break;
                }
        }
    }

    private static string? AnnotatedType(Node node) =>
        node.GetChildForField("type") is { } annotation ? (annotation.FirstNamedChild ?? annotation).Text : null;
}

internal sealed class PythonGrammar() : CodeGrammar("python")
{
    public override string Name => "python";

    public override bool BraceBodies => false;

    public override SymbolMatch? SymbolOf(Node node, CodeWalk walk)
    {
        string? kind = node.Type switch
        {
            "class_definition" => Class,
            "function_definition" when walk.Current >= 0 =>
                node.GetChildForField("name")?.Text == "__init__" ? Constructor : Method,
            "function_definition" => Function,
            _ => null,
        };
        if (kind is null)
            return null;
        return new SymbolMatch(kind, node.GetChildForField("name"), SpanOf(node), kind == Class ? null : node.GetChildForField("body"));
    }

    public override bool ElidesBody(Node body) => body.Type == "block";

    public override void Visit(Node node, CodeWalk walk)
    {
        switch (node.Type)
        {
            case "import_statement":
                foreach (Node child in node.NamedChildren)
                {
                    if (child.Type == "dotted_name")
                        walk.AddImport(new CodeImport(node.StartPosition.Row + 1, child.Text, []));
                    else if (child.Type == "aliased_import" && child.GetChildForField("name") is { } name)
                        walk.AddImport(new CodeImport(node.StartPosition.Row + 1, name.Text, [], child.GetChildForField("alias")?.Text));
                }
                break;
            case "import_from_statement" when node.GetChildForField("module_name") is { } module:
                {
                    var names = new List<string>();
                    if (node.NamedChildren.Any(c => c.Type == "wildcard_import"))
                        names.Add("*");
                    foreach (Node name in node.GetChildrenForField("name"))
                    {
                        names.Add(name.Type == "aliased_import"
                            ? $"{name.GetChildForField("name")?.Text} as {name.GetChildForField("alias")?.Text}"
                            : name.Text);
                    }
                    walk.AddImport(new CodeImport(node.StartPosition.Row + 1, module.Text, names));
                    break;
                }
            case "call":
                {
                    Node? function = node.GetChildForField("function");
                    if (function?.Type == "identifier")
                        walk.AddCall(function.Text, null, node);
                    else if (function?.Type == "attribute" && function.GetChildForField("object") is { } target)
                        walk.AddCall(function.GetChildForField("attribute")?.Text ?? "", CodeSyntax.Receiver(target), node);
                    break;
                }
            case "class_definition" when node.GetChildForField("superclasses") is { } superclasses:
                foreach (Node child in superclasses.NamedChildren.Where(c => c.Type is "identifier" or "attribute"))
                    walk.AddBase(child);
                break;
            case "typed_parameter" when node.FirstNamedChild is { Type: "identifier" } name && node.GetChildForField("type") is { } type:
                walk.AddVariable(name.Text, type.Text);
                break;
            case "typed_default_parameter" when node.GetChildForField("name") is { } name && node.GetChildForField("type") is { } type:
                walk.AddVariable(name.Text, type.Text);
                break;
            case "assignment" when node.GetChildForField("left") is { } left:
                {
                    string? type = node.GetChildForField("type")?.Text ?? ConstructedType(node.GetChildForField("right"));
                    if (type is null)
                        break;
                    if (left.Type == "identifier")
                        walk.AddVariable(left.Text, type);
                    else if (left.Type == "attribute" && left.GetChildForField("object")?.Text == "self"
                             && left.GetChildForField("attribute") is { } attribute)
                        walk.AddMemberVariable(attribute.Text, type);
                    break;
                }
        }
    }

    /// <summary>The class a value constructs, by the convention that classes are capitalised: <c>Foo()</c>, <c>mod.Foo()</c>.</summary>
    private static string? ConstructedType(Node? value)
    {
        if (value?.Type != "call" || value.GetChildForField("function") is not { Type: "identifier" or "attribute" } function)
            return null;
        string text = function.Text;
        int dot = text.LastIndexOf('.');
        return char.IsUpper(text[dot + 1]) ? text : null;
    }
}
