using MathNet.Numerics.Distributions;

namespace Connapse.Eval.Statistics;

public sealed record PairedComparison(
    int N,
    double MeanDifference,
    double CiLow,
    double CiHigh,
    double TTestP,
    double PermutationP,
    double EffectSizeDz,
    double MinimumDetectableEffect);

/// <summary>
/// Paired comparison of per-query scores (candidate − baseline): BCa bootstrap interval, paired
/// t-test, sign-flip permutation test, effect size d_z, and the minimum detectable effect at
/// α = 0.05 and 80% power. Urbano et al. 2019 favour the t-test and permutation test for decisions;
/// the bootstrap is used only for the interval.
/// <para>
/// The permutation test and the BCa interval are ported from SciPy 1.18.0,
/// scipy/stats/_resampling.py (<c>permutation_test</c> with <c>permutation_type="samples"</c>, and
/// <c>bootstrap</c> with <c>method="BCa"</c>), and means follow NumPy's summation order so results
/// agree with SciPy bit for bit on the same resamples (BSD-3-Clause; see THIRD_PARTY_NOTICES.md).
/// Verified against SciPy in ReferenceImplementationTests.
/// </para>
/// </summary>
public static class PairedStats
{
    public const int DefaultResamples = 10_000;
    public const int DefaultSeed = 20260925;

    // np.finfo(np.float64).eps
    private const double Float64Eps = 2.220446049250313e-16;

    public static PairedComparison Compare(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        int resamples = DefaultResamples,
        int seed = DefaultSeed)
    {
        if (baseline.Count != candidate.Count)
            throw new ArgumentException("Paired samples must have the same length.");
        int n = baseline.Count;
        if (n < 2)
            throw new ArgumentException("At least two paired observations are required.");

        double[] d = new double[n];
        for (int i = 0; i < n; i++)
            d[i] = candidate[i] - baseline[i];

        double mean = d.Average();
        double sd = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / (n - 1));
        double mde = (Normal.InvCDF(0, 1, 0.975) + Normal.InvCDF(0, 1, 0.8)) * sd / Math.Sqrt(n);
        double permutationP = PermutationPValue(d, resamples, new Random(seed + 1));

        if (sd == 0)
        {
            double dz = mean == 0 ? 0 : Math.Sign(mean) * double.PositiveInfinity;
            return new PairedComparison(n, mean, mean, mean, mean == 0 ? 1 : 0, permutationP, dz, mde);
        }

        double t = mean / (sd / Math.Sqrt(n));
        double tTestP = 2 * (1 - StudentT.CDF(0, 1, n - 1, Math.Abs(t)));
        (double low, double high) = BcaInterval(d, BootstrapResamples(n, resamples, new Random(seed)), 0.95);

        return new PairedComparison(n, mean, low, high, tTestP, permutationP, mean / sd, mde);
    }

    /// <summary>
    /// Two-sided paired permutation p-value for the mean of <paramref name="d"/>, following SciPy's
    /// <c>permutation_test(..., permutation_type="samples", alternative="two-sided")</c>: every
    /// sign pattern is enumerated when 2^n ≤ <paramref name="resamples"/> (exact test, no +1),
    /// otherwise <paramref name="resamples"/> random sign flips are drawn (+1 adjustment).
    /// </summary>
    public static double PermutationPValue(IReadOnlyList<double> d, int resamples, Random rng)
    {
        int n = d.Count;
        double observed = NumpyMean(d);

        // _calculate_null_pairings: n_max = factorial(2)**n; exact when n_permutations >= n_max.
        bool exact = n < 31 && (1L << n) <= resamples;
        int count = exact ? 1 << n : resamples;
        double[] nulls = new double[count];
        double[] flipped = new double[n];
        for (int b = 0; b < count; b++)
        {
            for (int i = 0; i < n; i++)
            {
                bool flip = exact ? ((b >> i) & 1) == 1 : rng.Next(2) == 1;
                flipped[i] = flip ? -d[i] : d[i];
            }
            nulls[b] = NumpyMean(flipped);
        }

        // permutation_test: adjustment = 0 if exact_test else 1; gamma = |eps * 100 * observed|.
        int adjustment = exact ? 0 : 1;
        double gamma = Math.Abs(Float64Eps * 100 * observed);
        int lessCount = nulls.Count(x => x <= observed + gamma);
        int greaterCount = nulls.Count(x => x >= observed - gamma);
        double pLess = (lessCount + adjustment) / (double)(count + adjustment);
        double pGreater = (greaterCount + adjustment) / (double)(count + adjustment);
        return Math.Clamp(Math.Min(pLess, pGreater) * 2, 0, 1);
    }

    /// <summary>
    /// BCa interval for the mean of <paramref name="d"/> over the given resamples (each row holds
    /// indices into <paramref name="d"/>), following SciPy's <c>_bca_interval</c> and
    /// <c>stats.quantile(method="linear")</c>. Returns NaN bounds where SciPy would (degenerate
    /// bootstrap distribution).
    /// </summary>
    public static (double Low, double High) BcaInterval(
        IReadOnlyList<double> d, IReadOnlyList<int[]> resamples, double confidenceLevel)
    {
        int n = d.Count;
        int bCount = resamples.Count;

        // theta_hat_b: the statistic on each bootstrap resample.
        double[] thetaHatB = new double[bCount];
        double[] resample = new double[n];
        for (int b = 0; b < bCount; b++)
        {
            for (int i = 0; i < n; i++)
                resample[i] = d[resamples[b][i]];
            thetaHatB[b] = NumpyMean(resample);
        }

        // z0_hat = ndtri(_percentile_of_score(theta_hat_b, theta_hat)), where the percentile uses the
        // 'mean' kind: (count(a < score) + count(a <= score)) / (2B).
        double thetaHat = NumpyMean(d);
        int below = thetaHatB.Count(x => x < thetaHat);
        int belowOrEqual = thetaHatB.Count(x => x <= thetaHat);
        double z0 = Normal.InvCDF(0, 1, (below + belowOrEqual) / (2.0 * bCount));

        // a_hat from jackknife resamples (each leaves out one observation).
        double[] thetaHatI = new double[n];
        double[] jackknife = new double[n - 1];
        for (int left = 0; left < n; left++)
        {
            for (int i = 0, j = 0; i < n; i++)
                if (i != left)
                    jackknife[j++] = d[i];
            thetaHatI[left] = NumpyMean(jackknife);
        }
        double thetaHatDot = NumpyMean(thetaHatI);
        double[] u = thetaHatI.Select(x => (n - 1) * (thetaHatDot - x)).ToArray();
        double num = NumpySum(u.Select(x => x * x * x).ToArray()) / Math.Pow(n, 3);
        double den = NumpySum(u.Select(x => x * x).ToArray()) / Math.Pow(n, 2);
        double aHat = 1.0 / 6 * num / Math.Pow(den, 1.5);

        double alpha = (1 - confidenceLevel) / 2;
        double zAlpha = Normal.InvCDF(0, 1, alpha);
        double num1 = z0 + zAlpha;
        double alpha1 = Normal.CDF(0, 1, z0 + (num1 / (1 - (aHat * num1))));
        double num2 = z0 - zAlpha;
        double alpha2 = Normal.CDF(0, 1, z0 + (num2 / (1 - (aHat * num2))));

        Array.Sort(thetaHatB);
        return (LinearQuantile(thetaHatB, alpha1), LinearQuantile(thetaHatB, alpha2));
    }

    private static int[][] BootstrapResamples(int n, int resamples, Random rng)
    {
        int[][] rows = new int[resamples][];
        for (int b = 0; b < resamples; b++)
        {
            rows[b] = new int[n];
            for (int i = 0; i < n; i++)
                rows[b][i] = rng.Next(n);
        }
        return rows;
    }

    /// <summary>scipy.stats.quantile(method="linear") on a sorted array (Hyndman–Fan type 7); NaN for NaN p.</summary>
    private static double LinearQuantile(double[] sorted, double p)
    {
        if (double.IsNaN(p))
            return double.NaN;
        double position = p * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double NumpyMean(IReadOnlyList<double> values) => NumpySum(values) / values.Count;

    /// <summary>
    /// np.add.reduce over a contiguous float64 array: NumPy's pairwise_sum over all elements
    /// (8 partial sums for up to 128 elements, halving above). Checked bit for bit against np.mean.
    /// </summary>
    private static double NumpySum(IReadOnlyList<double> values) => PairwiseSum(values, 0, values.Count);

    // Port of pairwise_sum in numpy/_core/src/umath/loops_utils.h.src (PW_BLOCKSIZE = 128).
    private static double PairwiseSum(IReadOnlyList<double> a, int start, int n)
    {
        if (n < 8)
        {
            double res = 0;
            for (int i = 0; i < n; i++)
                res += a[start + i];
            return res;
        }
        if (n <= 128)
        {
            double r0 = a[start], r1 = a[start + 1], r2 = a[start + 2], r3 = a[start + 3];
            double r4 = a[start + 4], r5 = a[start + 5], r6 = a[start + 6], r7 = a[start + 7];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
            {
                r0 += a[start + i];
                r1 += a[start + i + 1];
                r2 += a[start + i + 2];
                r3 += a[start + i + 3];
                r4 += a[start + i + 4];
                r5 += a[start + i + 5];
                r6 += a[start + i + 6];
                r7 += a[start + i + 7];
            }
            double res = ((r0 + r1) + (r2 + r3)) + ((r4 + r5) + (r6 + r7));
            for (; i < n; i++)
                res += a[start + i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a, start, n2) + PairwiseSum(a, start + n2, n - n2);
    }
}
