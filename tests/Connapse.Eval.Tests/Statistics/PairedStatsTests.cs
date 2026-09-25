using Connapse.Eval.Statistics;
using FluentAssertions;

namespace Connapse.Eval.Tests.Statistics;

[Trait("Category", "Unit")]
public class PairedStatsTests
{
    private static readonly double[] A = [0.50, 0.33, 1.00, 0.25, 0.00, 1.00, 0.50, 0.20, 1.00, 0.33, 0.00, 0.50];
    private static readonly double[] B = [1.00, 0.50, 1.00, 0.50, 0.25, 1.00, 1.00, 0.25, 1.00, 0.50, 0.00, 1.00];

    [Fact]
    public void Compare_ScipyReferenceSample_MatchesScipy()
    {
        PairedComparison result = PairedStats.Compare(A, B);

        result.N.Should().Be(12);
        result.MeanDifference.Should().BeApproximately(0.19916666666666663, 1e-12);
        result.TTestP.Should().BeApproximately(0.006181354358829799, 1e-8);
        result.MinimumDetectableEffect.Should().BeApproximately(0.16525749088269517, 1e-9);
        // BCa bounds depend on the random resamples; SciPy gave [0.0992, 0.3242].
        result.CiLow.Should().BeApproximately(0.0992, 0.02);
        result.CiHigh.Should().BeApproximately(0.3242, 0.02);
        result.PermutationP.Should().BeLessThan(0.05);
    }

    [Fact]
    public void Compare_IdenticalSamples_ReportsNoDifference()
    {
        PairedComparison result = PairedStats.Compare(A, A);

        result.MeanDifference.Should().Be(0);
        result.TTestP.Should().Be(1);
        result.PermutationP.Should().Be(1);
        result.CiLow.Should().Be(0);
        result.CiHigh.Should().Be(0);
    }

    [Fact]
    public void Compare_ZeroMeanNoise_IsNotSignificant()
    {
        double[] baseline = Enumerable.Repeat(0.5, 20).ToArray();
        double[] candidate = baseline.Select((v, i) => i % 2 == 0 ? v + 0.1 : v - 0.1).ToArray();

        PairedComparison result = PairedStats.Compare(baseline, candidate);

        result.TTestP.Should().BeApproximately(1.0, 1e-9);
        result.PermutationP.Should().BeGreaterThan(0.5);
        result.CiLow.Should().BeLessThanOrEqualTo(0);
        result.CiHigh.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Compare_ConsistentShift_IsSignificant()
    {
        double[] baseline = Enumerable.Repeat(0.4, 30).ToArray();
        double[] candidate = baseline.Select((v, i) => v + 0.2 + ((i % 3) - 1) * 0.01).ToArray();

        PairedComparison result = PairedStats.Compare(baseline, candidate);

        result.TTestP.Should().BeLessThan(0.001);
        result.PermutationP.Should().BeLessThan(0.001);
        result.CiLow.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Compare_SameInputsTwice_IsDeterministic()
    {
        PairedStats.Compare(A, B).Should().Be(PairedStats.Compare(A, B));
    }

    [Fact]
    public void Compare_MismatchedLengths_Throws()
    {
        Action act = () => PairedStats.Compare([0.1, 0.2], [0.1]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Adjust_TextbookExample_MatchesHolm()
    {
        double[] adjusted = Holm.Adjust([0.01, 0.04, 0.03, 0.005]);

        adjusted.Should().Equal([0.03, 0.06, 0.06, 0.02], (x, y) => Math.Abs(x - y) < 1e-12);
    }
}
