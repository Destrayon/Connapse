using MathNet.Numerics.Distributions;

namespace Connapse.Eval.Statistics;

/// <summary>
/// The paired tests the tables head-to-head pass bar names (CON-320): an exact McNemar test on right-or-wrong pairs, and
/// a Wilcoxon signed-rank test on paired counts.
/// <para>
/// <see cref="McNemarExact"/> is ported from statsmodels 0.15.0, statsmodels/stats/contingency_tables.py
/// (<c>mcnemar(table, exact=True)</c>). <see cref="WilcoxonPValue"/> is ported from SciPy 1.18.0,
/// scipy/stats/_wilcoxon.py (<c>wilcoxon(d)</c> with its defaults: <c>zero_method="wilcox"</c>,
/// <c>correction=False</c>, <c>alternative="two-sided"</c>, <c>method="auto"</c>) and scipy/stats/_hypotests.py
/// (<c>_get_wilcoxon_distr</c>). BSD-3-Clause; see THIRD_PARTY_NOTICES.md. Verified against both in
/// ReferenceImplementationTests.
/// </para>
/// </summary>
public static class PairedTests
{
    private const double Float64Eps = 2.220446049250313e-16;

    /// <summary>
    /// Two-sided exact McNemar p-value from the discordant pairs: <paramref name="onlyFirst"/> pairs the first arm got
    /// right and the second wrong, <paramref name="onlySecond"/> the reverse.
    /// </summary>
    public static double McNemarExact(int onlyFirst, int onlySecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(onlyFirst);
        ArgumentOutOfRangeException.ThrowIfNegative(onlySecond);

        // statistic = np.minimum(n1, n2); pvalue = stats.binom.cdf(statistic, n1 + n2, 0.5) * 2; np.minimum(pvalue, 1)
        int n = onlyFirst + onlySecond;
        int statistic = Math.Min(onlyFirst, onlySecond);
        return n == 0 ? 1 : Math.Min(Binomial.CDF(0.5, n, statistic) * 2, 1);
    }

    /// <summary>
    /// Two-sided Wilcoxon signed-rank p-value for the paired differences <paramref name="d"/>; NaN when every
    /// difference is zero, as SciPy gives. Zeros are dropped (Wilcox), ties take average ranks. As SciPy's
    /// <c>method="auto"</c> chooses: more than 50 differences use the normal approximation; otherwise no ties and no
    /// zeros use the exact null distribution, 13 or fewer enumerate every sign flip, and the rest use the normal
    /// approximation with the tie correction.
    /// </summary>
    public static double WilcoxonPValue(IReadOnlyList<double> d)
    {
        int n = d.Count;
        if (n == 0)
            return double.NaN;

        double[] nonZero = [.. d.Where(x => x != 0)];
        int count = nonZero.Length;
        (double[] ranks, int[] tieCounts) = AverageRanks([.. nonZero.Select(Math.Abs)]);
        double rPlus = RankSumPositive(nonZero, ranks);
        bool hasTies = tieCounts.Any(t => t > 1);
        bool hasZeros = count < n;

        string method = n > 50 ? "asymptotic" : !(hasTies || hasZeros) ? "exact" : n <= 13 ? "permutation" : "asymptotic";
        return method switch
        {
            "exact" => ExactPValue(rPlus, count),
            "permutation" => PermutationPValue(nonZero, ranks, rPlus, n),
            _ => AsymptoticPValue(rPlus, count, tieCounts),
        };
    }

    private static double RankSumPositive(IReadOnlyList<double> values, IReadOnlyList<double> ranks)
    {
        double sum = 0;
        for (int i = 0; i < values.Count; i++)
            if (values[i] > 0)
                sum += ranks[i];
        return sum;
    }

    /// <summary>scipy.stats rankdata(method="average"), with the size of each tie group.</summary>
    public static (double[] Ranks, int[] TieCounts) AverageRanks(IReadOnlyList<double> x)
    {
        int n = x.Count;
        int[] order = [.. Enumerable.Range(0, n).OrderBy(i => x[i])];
        double[] ranks = new double[n];
        List<int> ties = [];
        int start = 0;
        while (start < n)
        {
            int end = start;
            while (end + 1 < n && x[order[end + 1]] == x[order[start]])
                end++;
            int size = end - start + 1;

            // ordinal_ranks[i] + (counts - 1) / 2
            double rank = start + 1 + ((size - 1) / 2.0);
            for (int k = start; k <= end; k++)
                ranks[order[k]] = rank;
            ties.Add(size);
            start = end + 1;
        }
        return (ranks, [.. ties]);
    }

    /// <summary>
    /// <c>mn = count*(count+1)*0.25</c>; <c>se = sqrt((count*(count+1)*(2*count+1) - sum(t^3 - t)/2) / 24)</c>;
    /// <c>z = (r_plus - mn) / se</c>; two-sided <c>2 * min(norm.cdf(z), norm.sf(z))</c>.
    /// </summary>
    private static double AsymptoticPValue(double rPlus, int count, IReadOnlyList<int> tieCounts)
    {
        double mn = count * (count + 1.0) * 0.25;
        double se = count * (count + 1.0) * ((2.0 * count) + 1.0);
        double tieCorrect = tieCounts.Sum(t => ((double)t * t * t) - t);
        se = Math.Sqrt((se - (tieCorrect / 2)) / 24);
        double z = (rPlus - mn) / se;
        if (double.IsNaN(z))
            return double.NaN;
        double p = 2 * Math.Min(Normal.CDF(0, 1, z), 1 - Normal.CDF(0, 1, z));
        return Math.Clamp(p, 0, 1);
    }

    /// <summary>
    /// <c>2 * min(dist.sf(floor(r_plus)), dist.cdf(ceil(r_plus)))</c> clipped to [0, 1], where <c>dist</c> is the exact
    /// distribution of <c>r_plus</c> and its cdf and sf take the complement past the mean, as WilcoxonDistribution does.
    /// </summary>
    private static double ExactPValue(double rPlus, int count)
    {
        double[] pmf = Distribution(count);
        double mean = count * (count + 1) / 4.0;
        double Cdf1(int k) => pmf.Take(k + 1).Sum();
        double Sf1(int k) => k >= pmf.Length ? 0 : pmf.Skip(Math.Max(k, 0)).Sum();
        double Cdf(int k) => k <= mean ? Cdf1(k) : 1 - Sf1(k + 1);
        double Sf(int k) => k <= mean ? Sf1(k) : 1 - Cdf1(k - 1);

        double p = 2 * Math.Min(Sf((int)Math.Floor(rPlus)), Cdf((int)Math.Ceiling(rPlus)));
        return Math.Clamp(p, 0, 1);
    }

    /// <summary>_get_wilcoxon_distr: the probability of each r_plus from 0 to n(n+1)/2.</summary>
    public static double[] Distribution(int n)
    {
        double[] c = [1.0];
        for (int k = 1; k <= n; k++)
        {
            double[] prev = c;
            c = new double[(k * (k + 1) / 2) + 1];
            int m = prev.Length;
            for (int i = 0; i < m; i++)
                c[i] = prev[i] * 0.5;
            for (int i = 0; i < m; i++)
                c[c.Length - m + i] += prev[i] * 0.5;
        }
        return c;
    }

    /// <summary>
    /// permutation_test over every sign flip of the <paramref name="n"/> differences (2^n ≤ 9999, so it enumerates and
    /// makes no +1 adjustment) with r_plus as the statistic. A zero difference stays zero when flipped, so it only
    /// repeats each pattern; the ranks don't change with signs.
    /// </summary>
    private static double PermutationPValue(IReadOnlyList<double> nonZero, IReadOnlyList<double> ranks, double observed, int n)
    {
        int count = 1 << n;
        int zeros = n - nonZero.Count;
        int lessCount = 0;
        int greaterCount = 0;
        double gamma = Math.Abs(Float64Eps * 100 * observed);
        for (int b = 0; b < (1 << nonZero.Count); b++)
        {
            double statistic = 0;
            for (int i = 0; i < nonZero.Count; i++)
            {
                bool flip = ((b >> i) & 1) == 1;
                if ((flip ? -nonZero[i] : nonZero[i]) > 0)
                    statistic += ranks[i];
            }
            if (statistic <= observed + gamma)
                lessCount += 1 << zeros;
            if (statistic >= observed - gamma)
                greaterCount += 1 << zeros;
        }
        double pLess = lessCount / (double)count;
        double pGreater = greaterCount / (double)count;
        return Math.Clamp(Math.Min(pLess, pGreater) * 2, 0, 1);
    }
}
