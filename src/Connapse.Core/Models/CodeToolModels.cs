namespace Connapse.Core;

/// <summary>How <see cref="Interfaces.ICodeTools.FindSymbolAsync"/> reads its query.</summary>
public enum CodeMatch
{
    /// <summary>A name, qualified as far as the caller knows it: <c>Ledger.Post</c> finds <c>Acme.Billing.Ledger.Post</c>.</summary>
    Exact = 0,

    /// <summary>Words that must all appear in the symbol's qualified name, in any case.</summary>
    Words = 1,

    /// <summary>A description, matched against the code itself by the container's search.</summary>
    Meaning = 2,
}

/// <summary>Which links <see cref="Interfaces.ICodeTools.ReferencesAsync"/> counts.</summary>
public enum CodeReferenceKind
{
    /// <summary>Every file that names the symbol: calls, bases, and plain mentions.</summary>
    All = 0,

    /// <summary>Files that call or construct it.</summary>
    Calls = 1,

    /// <summary>Types that inherit from it.</summary>
    Inherits = 2,
}

/// <summary>
/// Where code questions are asked: one container or source, and the caller. <paramref name="UserId"/> is set by the
/// server from the authenticated caller, never taken from a request.
/// </summary>
public sealed record CodeScope(Guid OwnerId, Guid? UserId);

/// <summary>
/// One definition of a symbol. <paramref name="Symbol"/> is its qualified name as the code engine keys it
/// (<c>Acme.Billing.Ledger.Post</c>, <c>click.core.Parameter.value_from_envvar</c>, <c>source/core/Ky#Ky.create</c>).
/// </summary>
public sealed record CodeDefinition(
    string Symbol,
    string Kind,
    string Language,
    string Path,
    int StartLine,
    int? EndLine,
    string? Signature,
    string DocumentId);

/// <summary>A line of a file. <paramref name="Line"/> is null when the line's number is not known.</summary>
public sealed record CodeLine(int? Line, string Text);

/// <summary>A file in a ranked answer, with why it is there and the lines that show it.</summary>
public sealed record CodeFileMatch(string Path, string DocumentId, double Score, string Reason, IReadOnlyList<CodeLine> Lines);

/// <summary>Something a definition calls, constructs or inherits, and where that is defined.</summary>
public sealed record CodeUse(string Symbol, string Kind, string Path, int Line, string Type, double Confidence);

/// <summary>
/// What <see cref="Interfaces.ICodeTools.FindSymbolAsync"/> found. <paramref name="Uses"/> and the files beyond the
/// definitions' own are filled only when asked to follow the first definition's calls.
/// <paramref name="Denied"/> is true when access could not be checked; then nothing else is returned.
/// </summary>
public sealed record CodeSymbolResult(
    IReadOnlyList<CodeDefinition> Definitions,
    IReadOnlyList<CodeUse> Uses,
    IReadOnlyList<CodeFileMatch> Files,
    bool Denied = false)
{
    public static CodeSymbolResult DeniedResult { get; } = new([], [], [], Denied: true);
}

/// <summary>The files that refer to a symbol, ranked: linked references first, then files that only name it.</summary>
public sealed record CodeReferencesResult(
    IReadOnlyList<CodeDefinition> Definitions,
    IReadOnlyList<CodeFileMatch> Files,
    bool Denied = false)
{
    public static CodeReferencesResult DeniedResult { get; } = new([], [], Denied: true);
}

/// <summary>A symbol a change could affect: it reaches the changed one in <paramref name="Hops"/> calls or bases.</summary>
public sealed record CodeImpacted(string Symbol, string Kind, string Path, int Hops, double Confidence, bool IsTest);

/// <summary>What a change to a symbol could affect, the files that hold it, and which of those are tests.</summary>
public sealed record CodeImpactResult(
    IReadOnlyList<CodeDefinition> Definitions,
    IReadOnlyList<CodeImpacted> Symbols,
    IReadOnlyList<CodeFileMatch> Files,
    IReadOnlyList<string> Tests,
    bool Denied = false)
{
    public static CodeImpactResult DeniedResult { get; } = new([], [], [], [], Denied: true);
}

/// <summary>A definition on the repository map, and how many other files refer to it.</summary>
public sealed record CodeMapSymbol(string Symbol, string Kind, int Line, string? Signature, int ReferencedBy);

/// <summary>A file on the repository map, ranked by how much the rest of the code refers to what it defines.</summary>
public sealed record CodeMapFile(string Path, string DocumentId, double Rank, IReadOnlyList<CodeMapSymbol> Symbols);

/// <summary>The most referred-to files of a repository, or a folder of it, and their main definitions.</summary>
public sealed record CodeRepoMap(IReadOnlyList<CodeMapFile> Files, int TotalFiles, bool Denied = false)
{
    public static CodeRepoMap DeniedResult { get; } = new([], 0, Denied: true);
}
