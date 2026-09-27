namespace Connapse.Search.Keyword;

/// <summary>
/// A search-box query split the way Lucene, Elasticsearch and Tantivy read one: every word and
/// "quoted phrase" is an optional clause (a chunk needs any one of them, and more matches rank
/// higher), while -word and -"quoted phrase" are exclusions a chunk must not contain. Words like
/// "or" and "and" are ordinary text, not operators. See docs/research/keyword-query-matching-2026-09-26.md.
/// </summary>
internal sealed record KeywordQuery(IReadOnlyList<string> Clauses, IReadOnlyList<string> Exclusions)
{
    public static KeywordQuery Parse(string query)
    {
        List<string> clauses = [];
        List<string> exclusions = [];
        int i = 0;

        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i]))
            {
                i++;
                continue;
            }

            bool negated = query[i] == '-' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]);
            int start = negated ? i + 1 : i;
            string text;

            int close = query[start] == '"' ? query.IndexOf('"', start + 1) : -1;
            if (close > start)
            {
                text = query[(start + 1)..close];
                i = close + 1;
            }
            else
            {
                // An unmatched quote is just a character in a word.
                int end = start;
                while (end < query.Length && !char.IsWhiteSpace(query[end]))
                    end++;
                text = query[start..end].Trim('"');
                i = end;
            }

            if (!string.IsNullOrWhiteSpace(text))
                (negated ? exclusions : clauses).Add(text);
        }

        return new KeywordQuery(clauses, exclusions);
    }
}
