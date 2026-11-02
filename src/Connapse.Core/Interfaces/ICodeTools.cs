namespace Connapse.Core.Interfaces;

/// <summary>
/// Questions about code in one container or source, answered from the code links ingestion writes (CON-109) and the
/// code's own text (CON-37). Every answer holds only documents the caller may read; a check that cannot answer
/// returns the type's denied result.
/// </summary>
public interface ICodeTools
{
    /// <summary>
    /// The definitions <paramref name="query"/> means. With <paramref name="follow"/>, also what the first definition
    /// calls, constructs or inherits and the files defining those: what to read to see how it works.
    /// </summary>
    Task<CodeSymbolResult> FindSymbolAsync(CodeScope scope, string query, CodeMatch match, bool follow, int limit, CancellationToken ct = default);

    /// <summary>The files that refer to <paramref name="symbol"/>, best evidence first.</summary>
    Task<CodeReferencesResult> ReferencesAsync(CodeScope scope, string symbol, CodeReferenceKind kind, int limit, CancellationToken ct = default);

    /// <summary>What reaches <paramref name="symbol"/> through calls and bases, up to <paramref name="depth"/> hops, and which of it is tests.</summary>
    Task<CodeImpactResult> ImpactAsync(CodeScope scope, string symbol, int depth, int limit, CancellationToken ct = default);

    /// <summary>The files under <paramref name="pathPrefix"/> (all when null) most referred to, with their main definitions.</summary>
    Task<CodeRepoMap> RepoMapAsync(CodeScope scope, string? pathPrefix, int maxFiles, CancellationToken ct = default);
}
