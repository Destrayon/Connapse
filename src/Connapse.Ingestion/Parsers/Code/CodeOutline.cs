using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connapse.Ingestion.Parsers.Code;

/// <summary>
/// What <see cref="CodeParser"/> read from one source file: its definitions, imports, calls and declared types, by
/// syntax alone (CON-36). It crosses from the parser host in <see cref="ParsedDocumentMetadataKey"/>, so the chunker
/// can cut one passage per definition and the link builder can name what each one calls. Lines are 1-based.
/// </summary>
/// <remarks><paramref name="HasErrors"/> is true when part of the file could not be read as code at all.</remarks>
public sealed record CodeOutline(
    string Language,
    IReadOnlyList<CodeImport> Imports,
    IReadOnlyList<CodeSymbol> Symbols,
    IReadOnlyList<CodeCall> ModuleCalls,
    IReadOnlyList<CodeVariable> ModuleVariables,
    bool HasErrors)
{
    /// <summary>The parsed document's metadata key holding the outline as JSON.</summary>
    public const string ParsedDocumentMetadataKey = "CodeOutline";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The outline a parsed document carries, or null when it has none or it cannot be read.</summary>
    public static CodeOutline? From(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(ParsedDocumentMetadataKey, out string? json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CodeOutline>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A <c>using</c>, <c>import</c> or <c>from … import</c>. <paramref name="Target"/> is the namespace, module or
/// module path as written; <paramref name="Names"/> the names it brings in, each as <c>name</c> or <c>name as alias</c>
/// (empty for a C# <c>using</c> or a Python <c>import x</c>); <paramref name="Alias"/> a name the whole target is bound to.
/// </summary>
public sealed record CodeImport(int Line, string Target, IReadOnlyList<string> Names, string? Alias = null);

/// <summary>
/// A type or a callable member. <paramref name="Parent"/> is the index of the enclosing symbol, or -1.
/// <paramref name="Container"/> is the dotted name it sits in: namespace and enclosing types in C#, enclosing classes
/// elsewhere. <paramref name="StartLine"/> includes its doc comment, attributes and decorators. The lines from
/// <paramref name="ElideStart"/> to <paramref name="ElideEnd"/> are its body's inside, which a type's outline leaves
/// out (0 when there is nothing to leave out).
/// </summary>
public sealed record CodeSymbol(
    int Parent,
    string Kind,
    string Name,
    string Container,
    int StartLine,
    int EndLine,
    int ElideStart,
    int ElideEnd,
    string Signature,
    IReadOnlyList<string> Bases,
    IReadOnlyList<CodeCall> Calls,
    IReadOnlyList<CodeVariable> Variables)
{
    [JsonIgnore]
    public string QualifiedName => Container.Length == 0 ? Name : $"{Container}.{Name}";

    [JsonIgnore]
    public bool IsType => Kind is CodeKinds.Class or CodeKinds.Struct or CodeKinds.Record or CodeKinds.Interface or CodeKinds.EnumType;
}

/// <summary>
/// A call or a construction. <paramref name="Receiver"/> is what it was called on, as written (<c>this</c>,
/// <c>self</c>, <c>base</c>, <c>_store</c>, <c>Foo.Bar</c>), "?" for anything more complex, or null for a bare call.
/// </summary>
public sealed record CodeCall(string Name, string? Receiver, int Line, bool IsNew = false);

/// <summary>A parameter, local, field or property and its declared or constructed type, without generic arguments.</summary>
public sealed record CodeVariable(string Name, string Type);

public static class CodeKinds
{
    public const string Class = "class";
    public const string Struct = "struct";
    public const string Record = "record";
    public const string Interface = "interface";
    public const string EnumType = "enum";
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Property = "property";
    public const string Function = "function";
}
