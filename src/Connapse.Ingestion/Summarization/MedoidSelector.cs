namespace Connapse.Ingestion.Summarization;

public static class MedoidSelector
{
    public readonly record struct MedoidWithSize(Guid Id, int ClusterSize);
    public readonly record struct SelectionResult(IReadOnlyList<MedoidWithSize> Medoids);

    // Farthest-first traversal: pick first medoid as the doc farthest from
    // overall centroid; each subsequent medoid is the doc farthest from any
    // already-chosen medoid (max-min cosine distance).
    public static IReadOnlyList<(Guid Id, float[] Embedding)> SelectFarthestFirst(
        IReadOnlyList<(Guid Id, float[] Embedding)> docs,
        int k)
    {
        if (k <= 0) return Array.Empty<(Guid Id, float[] Embedding)>();
        if (docs.Count <= k) return docs;

        // Initial centroid: mean embedding
        int dim = docs[0].Embedding.Length;
        float[] centroid = new float[dim];
        foreach (var d in docs)
            for (int i = 0; i < dim; i++) centroid[i] += d.Embedding[i];
        for (int i = 0; i < dim; i++) centroid[i] /= docs.Count;

        // First medoid: farthest from centroid
        int firstIdx = 0;
        double firstDist = -1;
        for (int i = 0; i < docs.Count; i++)
        {
            double d = CosineDistance(docs[i].Embedding, centroid);
            if (d > firstDist) { firstDist = d; firstIdx = i; }
        }

        List<(Guid Id, float[] Embedding)> chosen = new() { docs[firstIdx] };
        HashSet<Guid> chosenIds = new() { docs[firstIdx].Id };

        while (chosen.Count < k)
        {
            int nextIdx = -1;
            double maxMinDist = -1;
            for (int i = 0; i < docs.Count; i++)
            {
                if (chosenIds.Contains(docs[i].Id)) continue;
                double minDist = chosen.Min(c => CosineDistance(docs[i].Embedding, c.Embedding));
                if (minDist > maxMinDist) { maxMinDist = minDist; nextIdx = i; }
            }
            if (nextIdx < 0) break;
            chosen.Add(docs[nextIdx]);
            chosenIds.Add(docs[nextIdx].Id);
        }

        return chosen;
    }

    public static SelectionResult SelectFarthestFirstWithAssignments(
        IReadOnlyList<(Guid Id, float[] Embedding)> docs,
        int k)
    {
        var medoids = SelectFarthestFirst(docs, k);
        Dictionary<Guid, int> counts = medoids.ToDictionary(m => m.Id, _ => 0);

        foreach (var doc in docs)
        {
            (Guid Id, float[] Embedding) nearest = medoids[0];
            double bestDist = double.MaxValue;
            foreach (var m in medoids)
            {
                double d = CosineDistance(doc.Embedding, m.Embedding);
                if (d < bestDist) { bestDist = d; nearest = m; }
            }
            counts[nearest.Id]++;
        }

        var result = medoids.Select(m => new MedoidWithSize(m.Id, counts[m.Id])).ToList();
        return new SelectionResult(result);
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        double sim = dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-12);
        return 1.0 - sim;
    }
}
