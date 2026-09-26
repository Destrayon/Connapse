namespace Connapse.Eval.Statistics;

/// <summary>
/// Holm–Bonferroni step-down correction.
/// Ported from statsmodels 0.15.0, statsmodels/stats/multitest.py, <c>multipletests(method="holm")</c>
/// (BSD-3-Clause; see THIRD_PARTY_NOTICES.md). Verified against it in ReferenceImplementationTests.
/// </summary>
public static class Holm
{
    /// <summary>Adjusted p-values, returned in the input order.</summary>
    public static double[] Adjust(IReadOnlyList<double> pValues)
    {
        int m = pValues.Count;

        // sortind = np.argsort(pvals); pvals = np.take(pvals, sortind)
        int[] sortind = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ToArray();

        // pvals_corrected = np.maximum.accumulate(pvals * np.arange(ntests, 0, -1))
        double[] corrected = new double[m];
        double running = double.NegativeInfinity;
        for (int k = 0; k < m; k++)
        {
            running = Math.Max(running, pValues[sortind[k]] * (m - k));
            corrected[k] = running;
        }

        // pvals_corrected[pvals_corrected > 1] = 1; then unsort: pvals_corrected_[sortind] = pvals_corrected
        double[] adjusted = new double[m];
        for (int k = 0; k < m; k++)
            adjusted[sortind[k]] = Math.Min(corrected[k], 1);
        return adjusted;
    }
}
