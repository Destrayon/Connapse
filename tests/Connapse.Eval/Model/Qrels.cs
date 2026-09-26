namespace Connapse.Eval.Model;

/// <summary>Graded relevance judgments: query ID → document ID → grade.</summary>
public sealed class Qrels
{
    private static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>();

    private readonly Dictionary<string, Dictionary<string, int>> _byQuery = new(StringComparer.Ordinal);

    public IEnumerable<string> QueryIds => _byQuery.Keys;

    public void Add(string queryId, string docId, int grade)
    {
        if (!_byQuery.TryGetValue(queryId, out Dictionary<string, int>? docs))
            _byQuery[queryId] = docs = new Dictionary<string, int>(StringComparer.Ordinal);
        docs[docId] = grade;
    }

    public IReadOnlyDictionary<string, int> For(string queryId) =>
        _byQuery.TryGetValue(queryId, out Dictionary<string, int>? docs) ? docs : Empty;

    public static Qrels ParseTrec(TextReader reader)
    {
        Qrels qrels = new();
        int lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 4 || !int.TryParse(fields[3], out int grade))
                throw new FormatException($"Malformed qrels line {lineNumber}: expected 'query 0 doc grade', got '{line}'.");
            if (qrels.For(fields[0]).ContainsKey(fields[2]))
                throw new FormatException($"Duplicate judgment for query '{fields[0]}' and document '{fields[2]}' on line {lineNumber}.");
            qrels.Add(fields[0], fields[2], grade);
        }
        return qrels;
    }

    public void WriteTrec(TextWriter writer)
    {
        foreach (string queryId in _byQuery.Keys.Order(StringComparer.Ordinal))
            foreach ((string docId, int grade) in _byQuery[queryId].OrderBy(p => p.Key, StringComparer.Ordinal))
                writer.Write($"{queryId} 0 {docId} {grade}\n");
    }
}
