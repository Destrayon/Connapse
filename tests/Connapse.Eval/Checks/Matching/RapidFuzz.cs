using System.Numerics;

namespace Connapse.Eval.Checks.Matching;

/// <summary>
/// <c>fuzz.ratio</c> and <c>fuzz.partial_ratio</c>, ported from RapidFuzz 3.14.6's pure-Python
/// implementation (<c>rapidfuzz/fuzz_py.py</c>, <c>distance/Indel_py.py</c>, <c>distance/LCSseq_py.py</c>;
/// MIT, see THIRD_PARTY_NOTICES.md). That implementation returned the same scores as RapidFuzz's C++
/// build on 3,000 benchmark-shaped cases. Strings are compared by code point, as Python does.
/// Verified against RapidFuzz in OlmOcrMatchingTests.
/// </summary>
public static class RapidFuzz
{
    /// <summary>Normalized Indel similarity × 100.</summary>
    public static double Ratio(string s1, string s2) => Ratio(PyText.CodePoints(s1), PyText.CodePoints(s2));

    public static double Ratio(int[] s1, int[] s2)
    {
        int maximum = s1.Length + s2.Length;
        if (maximum == 0)
            return 100.0;
        int dist = maximum - (2 * LcsLength(Block(s1), s1.Length, s2));
        return (1.0 - ((double)dist / maximum)) * 100;
    }

    /// <summary>The best <see cref="Ratio"/> of the shorter string against any alignment in the longer.</summary>
    public static double PartialRatio(string s1, string s2) => PartialRatio(PyText.CodePoints(s1), PyText.CodePoints(s2));

    public static double PartialRatio(int[] s1, int[] s2)
    {
        if (s1.Length == 0 && s2.Length == 0)
            return 100.0;
        (int[] shorter, int[] longer) = s1.Length <= s2.Length ? (s1, s2) : (s2, s1);
        double score = PartialRatioImpl(shorter, longer);
        if (score != 100 && s1.Length == s2.Length)
        {
            double swapped = PartialRatioImpl(longer, shorter);
            if (swapped > score)
                score = swapped;
        }
        return score;
    }

    // _partial_ratio_impl; assumes s1.Length <= s2.Length. Scores are in [0, 1] until the end.
    private static double PartialRatioImpl(int[] s1, int[] s2)
    {
        // Python reaches every window with an empty character set and scores 0; the early return
        // avoids s2[-1], which Python reads as the last item and .NET rejects.
        if (s1.Length == 0)
            return 0;
        HashSet<int> s1Chars = [.. s1];
        int len1 = s1.Length;
        int len2 = s2.Length;
        Dictionary<int, ulong[]> block = Block(s1);
        double best = 0;

        for (int i = 1; i < len1; i++)
        {
            if (!s1Chars.Contains(s2[i - 1]))
                continue;
            double ratio = BlockNormalizedSimilarity(block, len1, s2.AsSpan(0, Math.Min(i, len2)));
            if (ratio > best)
            {
                best = ratio;
                if (best == 1)
                    return 100;
            }
        }

        for (int i = 0; i < len2 - len1; i++)
        {
            if (!s1Chars.Contains(s2[i + len1 - 1]))
                continue;
            double ratio = BlockNormalizedSimilarity(block, len1, s2.AsSpan(i, len1));
            if (ratio > best)
            {
                best = ratio;
                if (best == 1)
                    return 100;
            }
        }

        for (int i = Math.Max(0, len2 - len1); i < len2; i++)
        {
            if (!s1Chars.Contains(s2[i]))
                continue;
            double ratio = BlockNormalizedSimilarity(block, len1, s2.AsSpan(i));
            if (ratio > best)
            {
                best = ratio;
                if (best == 1)
                    return 100;
            }
        }

        return best * 100;
    }

    private static double BlockNormalizedSimilarity(Dictionary<int, ulong[]> block, int len1, ReadOnlySpan<int> s2)
    {
        int maximum = len1 + s2.Length;
        int dist = maximum - (2 * LcsLength(block, len1, s2));
        double normDist = maximum == 0 ? 0 : (double)dist / maximum;
        return 1.0 - normDist;
    }

    private static Dictionary<int, ulong[]> Block(int[] s1)
    {
        int words = Math.Max(1, (s1.Length + 63) / 64);
        Dictionary<int, ulong[]> block = [];
        for (int i = 0; i < s1.Length; i++)
        {
            if (!block.TryGetValue(s1[i], out ulong[]? bits))
                block[s1[i]] = bits = new ulong[words];
            bits[i / 64] |= 1UL << (i % 64);
        }
        return block;
    }

    // Bit-parallel LCS length (Hyyrö), the multi-word form of LCSseq_py._block_similarity:
    // S = (S + U) | (S - U) with U = S & Matches; S - U never borrows because U ⊆ S.
    private static int LcsLength(Dictionary<int, ulong[]> block, int len1, ReadOnlySpan<int> s2)
    {
        if (len1 == 0)
            return 0;
        int words = (len1 + 63) / 64;
        ulong[] s = new ulong[words];
        Array.Fill(s, ulong.MaxValue);
        foreach (int ch in s2)
        {
            if (!block.TryGetValue(ch, out ulong[]? matches))
                continue;
            ulong carry = 0;
            for (int w = 0; w < words; w++)
            {
                ulong u = s[w] & matches[w];
                ulong sum = s[w] + u;
                ulong carryOut = sum < s[w] ? 1UL : 0UL;
                ulong withCarry = sum + carry;
                carryOut |= withCarry < sum ? 1UL : 0UL;
                s[w] = withCarry | (s[w] ^ u);
                carry = carryOut;
            }
        }

        int zeros = 0;
        for (int w = 0; w < words; w++)
        {
            int bits = w == words - 1 && len1 % 64 != 0 ? len1 % 64 : 64;
            ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            zeros += BitOperations.PopCount(~s[w] & mask);
        }
        return zeros;
    }
}
