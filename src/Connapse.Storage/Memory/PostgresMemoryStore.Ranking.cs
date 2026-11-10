using System.Linq.Expressions;
using Connapse.Core.Utilities;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Storage.Memory;

// Relevance for a recall of one space. Each query word weighs by how rare it is among the space's current facts
// (inverse document frequency), so the one word that picks a fact outranks a word most facts share. A word that matches
// no fact is tried as a prefix ("Postgres" finds "PostgreSQL"), then against the space's words by trigram similarity
// ("PostgreSQL" finds "Postgres"); those matches weigh less than a whole-word match. The subjects that match best bring
// their other current facts with them, ranked below the facts that matched themselves, so a subject found by what it is
// ("SSO gateway") shows the rest of what memory holds about it.
public sealed partial class PostgresMemoryStore
{
    /// <summary>What a recall's words match and how each matching fact scores.</summary>
    internal sealed record SearchPlan(
        string Words,
        Expression<Func<MemoryFactEntity, bool>> Where,
        Expression<Func<MemoryFactEntity, float>> Score);

    /// <summary>One tsquery term and its weight.</summary>
    internal sealed record SearchTerm(string Query, float Weight);

    /// <summary>A word reached by prefix or trigram weighs this share of a whole-word match.</summary>
    internal const float PartialWeight = 0.6f;

    /// <summary>A subject's other facts score this share of its best match.</summary>
    internal const float SiblingShare = 0.5f;

    /// <summary>At most this many subjects bring their other facts.</summary>
    internal const int SiblingSubjects = 3;

    /// <summary>A subject brings its other facts only when its best match scores at least this share of the top one.</summary>
    internal const float SiblingCutoff = 0.5f;

    /// <summary>The least trigram similarity at which a word stands in for a query word that matched nothing.</summary>
    internal const double TrigramThreshold = 0.5;

    private const int MinPartialLength = 4;

    /// <summary>
    /// The plan for <paramref name="text"/> in one container, or null when it has no words. <paramref name="rows"/> are
    /// the facts the caller may see, which decide the subjects that bring their other facts.
    /// </summary>
    private static async Task<SearchPlan?> PlanAsync(
        KnowledgeDbContext context, Guid containerId, IQueryable<MemoryFactEntity> rows, string? text, DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<string> words = QueryWords(text);
        if (words.Count == 0)
            return null;
        List<TermCount> counts = await CountTermsAsync(context, containerId, words, ct);
        List<SearchTerm> terms = Terms(words, counts, out List<string> unmatched);
        if (unmatched.Count > 0)
            terms.AddRange(await SimilarTermsAsync(context, containerId, unmatched, ct));

        // A word most facts share still ranks (with little weight) but no longer decides what matches, as before.
        HashSet<string> matching = [.. Distinctive(words, [.. counts.Select(c => new WordCount { Word = c.Word, Matches = c.Matches, Facts = c.Facts })])];
        List<SearchTerm> filter = [.. terms.Where(t => !words.Contains(t.Query) || matching.Contains(t.Query))];
        if (filter.Count == 0)
            filter = terms;
        if (terms.Count == 0)
            terms = filter = [.. words.Select(w => new SearchTerm(w, 1f))];

        ParameterExpression f = Expression.Parameter(typeof(MemoryFactEntity), "f");
        Expression own = OwnScore(f, terms);
        Expression matches = Expression.Invoke(MatchesTemplate, f, Parameter(string.Join(" | ", filter.Select(t => t.Query))));
        Expression<Func<MemoryFactEntity, bool>> direct = Expression.Lambda<Func<MemoryFactEntity, bool>>(Inline(matches), f);
        Expression<Func<MemoryFactEntity, float>> ownScore = Expression.Lambda<Func<MemoryFactEntity, float>>(own, f);

        List<(string Subject, float Best)> subjects = await TopSubjectsAsync(rows.Where(direct), ownScore, ct);
        if (subjects.Count == 0)
            return new SearchPlan(string.Join(' ', words), direct, ownScore);

        Expression boost = Parameter(0f);
        foreach ((string subject, float best) in subjects)
            boost = Expression.Condition(Inline(Expression.Invoke(SubjectIsTemplate, f, Parameter(subject))), Parameter(best * SiblingShare), boost);
        Expression sibling = Inline(Expression.Invoke(SubjectInTemplate, f, Parameter(subjects.Select(s => s.Subject).ToArray()), Parameter(now)));
        return new SearchPlan(
            string.Join(' ', words),
            Expression.Lambda<Func<MemoryFactEntity, bool>>(Expression.OrElse(direct.Body, sibling), f),
            Expression.Lambda<Func<MemoryFactEntity, float>>(Expression.Add(own, boost), f));
    }

    /// <summary>
    /// The terms the counted words give: a word that matches a fact weighs its inverse document frequency; one that matches
    /// none is tried as a prefix; a stop word drops out. The words that still match nothing are returned for trigrams.
    /// </summary>
    internal static List<SearchTerm> Terms(IReadOnlyList<string> words, IReadOnlyList<TermCount> counts, out List<string> unmatched)
    {
        List<SearchTerm> terms = [];
        unmatched = [];
        foreach (string word in words)
        {
            TermCount? c = counts.FirstOrDefault(c => c.Word == word);
            if (c is null || c.Stop)
                continue;
            if (c.Matches > 0)
                terms.Add(new SearchTerm(word, Idf(c.Facts, c.Matches)));
            else if (c.PrefixMatches > 0 && word.Length >= MinPartialLength)
                terms.Add(new SearchTerm(word + ":*", Idf(c.Facts, c.PrefixMatches) * PartialWeight));
            else if (word.Length >= MinPartialLength)
                unmatched.Add(word);
        }
        return terms;
    }

    /// <summary>The BM25 form of inverse document frequency, which stays positive however common the word is.</summary>
    internal static float Idf(int facts, int matches) =>
        (float)Math.Log(1 + (facts - matches + 0.5) / (matches + 0.5));

    /// <summary>
    /// For each word, how many current facts match it as a word and as a prefix, and whether it is a stop word. One pass
    /// over the space's current facts, with each word's query built once.
    /// </summary>
    private static async Task<List<TermCount>> CountTermsAsync(
        KnowledgeDbContext context, Guid containerId, IReadOnlyList<string> words, CancellationToken ct)
    {
        string[] all = [.. words];
        int minLength = MinPartialLength;
        return await context.Database.SqlQuery<TermCount>($"""
            WITH w AS MATERIALIZED (
                SELECT word, to_tsquery('english', word) AS q,
                       CASE WHEN length(word) >= {minLength} THEN to_tsquery('english', word || ':*') END AS p
                FROM unnest({all}) AS word)
            SELECT w.word AS "Word", numnode(w.q) = 0 AS "Stop",
                   count(f.id) FILTER (WHERE f.search_vector @@ w.q)::int AS "Matches",
                   count(f.id) FILTER (WHERE w.p IS NOT NULL AND f.search_vector @@ w.p)::int AS "PrefixMatches",
                   count(f.id)::int AS "Facts"
            FROM w LEFT JOIN memory_facts f ON f.container_id = {containerId} AND f.superseded_at IS NULL
            GROUP BY w.word, w.q
            """).ToListAsync(ct);
    }

    /// <summary>
    /// The space's words most like each unmatched word, at most two per word, weighted as partial matches. The space's
    /// words are its facts' lexemes, which hold only letters and digits since punctuation became spaces before parsing.
    /// </summary>
    private static async Task<List<SearchTerm>> SimilarTermsAsync(
        KnowledgeDbContext context, Guid containerId, IReadOnlyList<string> unmatched, CancellationToken ct)
    {
        int minLength = MinPartialLength;
        List<string> lexemes = await context.Database.SqlQuery<string>($"""
            SELECT DISTINCT u.lexeme AS "Value"
            FROM memory_facts f, unnest(f.search_vector) AS u
            WHERE f.container_id = {containerId} AND f.superseded_at IS NULL AND length(u.lexeme) >= {minLength}
            """).ToListAsync(ct);
        List<string> chosen = [.. unmatched.SelectMany(w => Similar(w, lexemes)).Distinct(StringComparer.Ordinal)];
        if (chosen.Count == 0)
            return [];
        List<TermCount> counts = await CountTermsAsync(context, containerId, chosen, ct);
        return [.. counts.Where(c => c.Matches > 0).Select(c => new SearchTerm(c.Word, Idf(c.Facts, c.Matches) * PartialWeight))];
    }

    /// <summary>Up to two of <paramref name="candidates"/> at least <see cref="TrigramThreshold"/> similar to the word, most similar first.</summary>
    internal static IEnumerable<string> Similar(string word, IEnumerable<string> candidates)
    {
        HashSet<string> mine = Trigrams.Of(word);
        return candidates
            .Where(c => QueryWords(c).Count == 1 && QueryWords(c)[0] == c)
            .Select(c => (Word: c, Score: Trigrams.Similarity(mine, Trigrams.Of(c))))
            .Where(c => c.Score >= TrigramThreshold && c.Word != word)
            .OrderByDescending(c => c.Score).ThenBy(c => c.Word, StringComparer.Ordinal)
            .Take(2)
            .Select(c => c.Word);
    }

    /// <summary>The best-matching subjects, best first: at most <see cref="SiblingSubjects"/>, each within <see cref="SiblingCutoff"/> of the top.</summary>
    private static async Task<List<(string Subject, float Best)>> TopSubjectsAsync(
        IQueryable<MemoryFactEntity> matching, Expression<Func<MemoryFactEntity, float>> score, CancellationToken ct)
    {
        ParameterExpression f = score.Parameters[0];
        Expression<Func<MemoryFactEntity, SubjectScore>> project = Expression.Lambda<Func<MemoryFactEntity, SubjectScore>>(
            Expression.MemberInit(Expression.New(typeof(SubjectScore)),
                Expression.Bind(typeof(SubjectScore).GetProperty(nameof(SubjectScore.Subject))!, Inline(Expression.Invoke(LowerSubjectTemplate, f))),
                Expression.Bind(typeof(SubjectScore).GetProperty(nameof(SubjectScore.Score))!, score.Body)),
            f);
        var best = await matching.Select(project)
            .GroupBy(s => s.Subject)
            .Select(g => new { Subject = g.Key, Best = g.Max(s => s.Score) })
            .OrderByDescending(g => g.Best).ThenBy(g => g.Subject)
            .Take(SiblingSubjects)
            .ToListAsync(ct);
        if (best.Count == 0 || best[0].Best <= 0)
            return [];
        float top = best[0].Best;
        return [.. best.Where(b => b.Best >= top * SiblingCutoff).Select(b => (b.Subject, b.Best))];
    }

    /// <summary>
    /// Σ weight × ts_rank(term) over the terms, as a single-precision sum so the keyset compares what SQL computed. No
    /// literal zero starts the sum: Postgres would read <c>0 + real</c> as double precision, and a fact tied at a page's
    /// edge would then compare above its own rounded cursor and be skipped.
    /// </summary>
    private static Expression OwnScore(ParameterExpression f, IReadOnlyList<SearchTerm> terms)
    {
        Expression? sum = null;
        foreach (SearchTerm t in terms)
        {
            Expression term = Expression.Multiply(Parameter(t.Weight), Inline(Expression.Invoke(RankTemplate, f, Parameter(t.Query))));
            sum = sum is null ? term : Expression.Add(sum, term);
        }
        return sum ?? Parameter(0f);
    }

    private static readonly Expression<Func<MemoryFactEntity, string, float>> RankTemplate =
        (f, q) => f.SearchVector.Rank(EF.Functions.ToTsQuery("english", q));

    private static readonly Expression<Func<MemoryFactEntity, string, bool>> MatchesTemplate =
        (f, q) => f.SearchVector.Matches(EF.Functions.ToTsQuery("english", q));

    // Translated to SQL lower(), which follows the database's collation, so the culture analyzers do not apply.
#pragma warning disable CA1304, CA1311, CA1862
    private static readonly Expression<Func<MemoryFactEntity, string>> LowerSubjectTemplate = f => f.Subject.ToLower();

    private static readonly Expression<Func<MemoryFactEntity, string, bool>> SubjectIsTemplate = (f, s) => f.Subject.ToLower() == s;

    // A subject brings only its current facts, even when the recall asks for history: what it holds now, not what it held.
    private static readonly Expression<Func<MemoryFactEntity, string[], DateTimeOffset, bool>> SubjectInTemplate = (f, s, now) =>
        s.Contains(f.Subject.ToLower()) && f.SupersededAt == null
        && (f.ValidFrom == null || f.ValidFrom <= now) && (f.ValidTo == null || f.ValidTo > now);
#pragma warning restore CA1304, CA1311, CA1862

    /// <summary>Replaces an invocation of a template lambda with its body, its parameters bound to the invocation's arguments.</summary>
    private static Expression Inline(Expression expression) => new Inliner().Visit(expression);

    private sealed class Inliner : ExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, Expression> bound = [];

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            if (node.Expression is not LambdaExpression lambda)
                return base.VisitInvocation(node);
            for (int i = 0; i < lambda.Parameters.Count; i++)
                bound[lambda.Parameters[i]] = Visit(node.Arguments[i]);
            return Visit(lambda.Body);
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            bound.TryGetValue(node, out Expression? value) ? value : node;
    }

    internal sealed class TermCount
    {
        public string Word { get; init; } = "";
        public bool Stop { get; init; }
        public int Matches { get; init; }
        public int PrefixMatches { get; init; }
        public int Facts { get; init; }
    }

    private sealed class SubjectScore
    {
        public string Subject { get; init; } = "";
        public float Score { get; init; }
    }
}
