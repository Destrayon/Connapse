using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Connapse.Storage.Memory;

// The facts a save might repeat or contradict (CON-332): one search seeded by the words Postgres stemmed for the new row.
public sealed partial class PostgresMemoryStore
{
    /// <summary>The most stemmed words a save's search asks for.</summary>
    private const int MaxMatchWords = 32;

    public async Task<MemoryMatchSearch> MatchCandidatesAsync(MemoryFact saved, MemoryCaller viewer, int take, CancellationToken ct = default)
    {
        await using KnowledgeDbContext context = await contextFactory.CreateDbContextAsync(ct);
        NpgsqlTsVector? vector = await context.MemoryFacts.AsNoTracking()
            .Where(f => f.Id == saved.Id).Select(f => f.SearchVector).FirstOrDefaultAsync(ct);
        if (vector is null)
            return MemoryMatchSearch.None;
        MemoryMatchWords words = WordsOf(vector);

        DateTimeOffset now = clock.GetUtcNow();
        IQueryable<MemoryFactEntity> rows = context.MemoryFacts.AsNoTracking().Where(f =>
            f.ContainerId == saved.ContainerId && f.ChainId != saved.ChainId && f.SupersededAt == null
            && (f.ValidFrom == null || f.ValidFrom <= now) && (f.ValidTo == null || f.ValidTo > now));
        rows = OwnOrTeam(rows, viewer);

#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(), which follows the database's collation.
        string subject = saved.Subject.ToLower();
        string? query = MatchQuery([.. words.Subject, .. words.Attribute, .. words.Value]);
        var found = query is null
            ? await rows.Where(f => f.Subject.ToLower() == subject)
                .OrderByDescending(f => f.RecordedAt).ThenBy(f => f.Id).Take(take)
                .Select(f => new { Row = f, f.Sources, Rank = 1f }).ToListAsync(ct)
            : await rows.Where(f => f.Subject.ToLower() == subject || f.SearchVector.Matches(EF.Functions.ToTsQuery("simple", query)))
                .Select(f => new
                {
                    Row = f,
                    f.Sources,
                    Same = f.Subject.ToLower() == subject,
                    Rank = f.SearchVector.Rank(EF.Functions.ToTsQuery("simple", query)),
                })
                .OrderByDescending(m => m.Same).ThenByDescending(m => m.Rank).ThenByDescending(m => m.Row.RecordedAt).ThenBy(m => m.Row.Id)
                .Take(take)
                .Select(m => new { m.Row, m.Sources, m.Rank }).ToListAsync(ct);
#pragma warning restore CA1304, CA1311, CA1862

        return new MemoryMatchSearch(words, [.. found.Select(m =>
        {
            m.Row.Sources = m.Sources;
            return new MemoryMatchCandidate(ToFact(m.Row), WordsOf(m.Row.SearchVector), m.Rank);
        })]);
    }

    /// <summary>Team facts and the viewer's own, by <see cref="MemoryCaller.IsAuthorOf"/>'s rule; sources are checked by the caller.</summary>
    private static IQueryable<MemoryFactEntity> OwnOrTeam(IQueryable<MemoryFactEntity> rows, MemoryCaller viewer)
    {
        const short team = (short)MemoryScope.Team;
        if (viewer.UserId is { } user)
            return rows.Where(f => f.Scope == team || f.AuthorUserId == user);
        if (viewer.AgentId is { } agent)
            return rows.Where(f => f.Scope == team || (f.AuthorUserId == null && f.AuthorAgentId == agent));
        return rows.Where(f => f.Scope == team);
    }

    /// <summary>
    /// The stemmed words OR-ed into a 'simple' tsquery, which matches them exactly as stored. Each is quoted, so none
    /// is read as tsquery syntax. Null when there are none.
    /// </summary>
    internal static string? MatchQuery(IEnumerable<string> lexemes)
    {
        List<string> quoted = [.. lexemes.Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxMatchWords)
            .Select(l => "'" + l.Replace("'", "''", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal) + "'")];
        return quoted.Count == 0 ? null : string.Join(" | ", quoted);
    }

    /// <summary>A stored search vector's words by field: weight A is the subject's, B the attribute's, C the value's.</summary>
    internal static MemoryMatchWords WordsOf(NpgsqlTsVector vector)
    {
        List<string> subject = [], attribute = [], value = [];
        foreach (NpgsqlTsVector.Lexeme lexeme in vector)
        {
            HashSet<NpgsqlTsVector.Lexeme.Weight> weights = [];
            for (int i = 0; i < lexeme.Count; i++)
                weights.Add(lexeme[i].Weight);
            if (weights.Contains(NpgsqlTsVector.Lexeme.Weight.A))
                subject.Add(lexeme.Text);
            if (weights.Contains(NpgsqlTsVector.Lexeme.Weight.B))
                attribute.Add(lexeme.Text);
            if (weights.Contains(NpgsqlTsVector.Lexeme.Weight.C))
                value.Add(lexeme.Text);
        }
        return new MemoryMatchWords(subject, attribute, value);
    }
}
