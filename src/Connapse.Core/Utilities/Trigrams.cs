namespace Connapse.Core.Utilities;

/// <summary>Trigram similarity as pg_trgm computes it for single words: shared trigrams over all trigrams of both.</summary>
public static class Trigrams
{
    public static HashSet<string> Of(string word)
    {
        string padded = "  " + word.ToLowerInvariant() + " ";
        HashSet<string> set = new(StringComparer.Ordinal);
        for (int i = 0; i + 3 <= padded.Length; i++)
            set.Add(padded.Substring(i, 3));
        return set;
    }

    public static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        int shared = a.Count(b.Contains);
        int all = a.Count + b.Count - shared;
        return all == 0 ? 0 : (double)shared / all;
    }

    public static double Similarity(string a, string b) => Similarity(Of(a), Of(b));
}
