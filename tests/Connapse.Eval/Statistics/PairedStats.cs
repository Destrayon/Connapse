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
/// </summary>
public static class PairedStats
{
    public const int DefaultResamples = 10_000;
    public const int DefaultSeed = 20260925;

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
        double permutationP = SignFlipPValue(d, mean, resamples, new Random(seed + 1));

        if (sd == 0)
        {
            double dz = mean == 0 ? 0 : Math.Sign(mean) * double.PositiveInfinity;
            return new PairedComparison(n, mean, mean, mean, mean == 0 ? 1 : 0, permutationP, dz, mde);
        }

        double t = mean / (sd / Math.Sqrt(n));
        double tTestP = 2 * (1 - StudentT.CDF(0, 1, n - 1, Math.Abs(t)));
        (double low, double high) = BcaInterval(d, mean, resamples, new Random(seed));

        return new PairedComparison(n, mean, low, high, tTestP, permutationP, mean / sd, mde);
    }

    private static double SignFlipPValue(double[] d, double mean, int resamples, Random rng)
    {
        double observed = Math.Abs(mean);
        int extreme = 0;
        for (int b = 0; b < resamples; b++)
        {
            double sum = 0;
            for (int i = 0; i < d.Length; i++)
                sum += rng.Next(2) == 0 ? d[i] : -d[i];
            if (Math.Abs(sum / d.Length) >= observed - 1e-12)
                extreme++;
        }
        return (extreme + 1.0) / (resamples + 1.0);
    }

    private static (double Low, double High) BcaInterval(double[] d, double mean, int resamples, Random rng)
    {
        int n = d.Length;
        double[] boot = new double[resamples];
        for (int b = 0; b < resamples; b++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += d[rng.Next(n)];
            boot[b] = sum / n;
        }
        Array.Sort(boot);

        double below = boot.Count(x => x < mean);
        double proportion = Math.Clamp(below / resamples, 1.0 / (resamples + 1), resamples / (resamples + 1.0));
        double z0 = Normal.InvCDF(0, 1, proportion);

        double total = d.Sum();
        double[] jackknife = d.Select(x => (total - x) / (n - 1)).ToArray();
        double jackMean = jackknife.Average();
        double numerator = jackknife.Sum(j => Math.Pow(jackMean - j, 3));
        double denominator = 6 * Math.Pow(jackknife.Sum(j => Math.Pow(jackMean - j, 2)), 1.5);
        double acceleration = denominator == 0 ? 0 : numerator / denominator;

        double Adjusted(double alpha)
        {
            double z = Normal.InvCDF(0, 1, alpha);
            return Normal.CDF(0, 1, z0 + ((z0 + z) / (1 - (acceleration * (z0 + z)))));
        }

        return (Percentile(boot, Adjusted(0.025)), Percentile(boot, Adjusted(0.975)));
    }

    /// <summary>Linear interpolation between order statistics (NumPy's default).</summary>
    private static double Percentile(double[] sorted, double p)
    {
        double position = p * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }
}
