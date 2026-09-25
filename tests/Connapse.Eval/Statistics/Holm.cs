namespace Connapse.Eval.Statistics;

public static class Holm
{
    /// <summary>Holm–Bonferroni adjusted p-values, returned in the input order.</summary>
    public static double[] Adjust(IReadOnlyList<double> pValues)
    {
        int m = pValues.Count;
        int[] order = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ToArray();
        double[] adjusted = new double[m];
        double running = 0;
        for (int rank = 0; rank < m; rank++)
        {
            int index = order[rank];
            running = Math.Max(running, Math.Min(1, (m - rank) * pValues[index]));
            adjusted[index] = running;
        }
        return adjusted;
    }
}
