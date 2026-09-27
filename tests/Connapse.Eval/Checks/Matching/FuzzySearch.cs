namespace Connapse.Eval.Checks.Matching;

public readonly record struct NearMatch(int Start, int End, int Dist);

/// <summary>
/// <c>fuzzysearch.find_near_matches(subsequence, sequence, max_l_dist=n)</c>, ported from fuzzysearch
/// 0.8.1 (<c>__init__.py</c>, <c>levenshtein.py</c>, <c>levenshtein_ngram.py</c>, <c>common.py</c>,
/// <c>search_exact.py</c>; MIT, see THIRD_PARTY_NOTICES.md). Positions are code-point indexes.
/// <para>
/// One deliberate difference: when several overlapping matches tie on distance and length, fuzzysearch
/// keeps whichever its <c>set</c> yields first, which depends on CPython's hashing; this port keeps the
/// one that starts first. Verified against fuzzysearch in OlmOcrMatchingTests.
/// </para>
/// </summary>
public static class FuzzySearch
{
    public static IReadOnlyList<NearMatch> FindNearMatches(string subsequence, string sequence, int maxLDist) =>
        FindNearMatches(PyText.CodePoints(subsequence), PyText.CodePoints(sequence), maxLDist);

    public static IReadOnlyList<NearMatch> FindNearMatches(int[] sub, int[] seq, int maxLDist)
    {
        if (sub.Length == 0)
            throw new ArgumentException("subsequence must not be empty", nameof(sub));
        ArgumentOutOfRangeException.ThrowIfNegative(maxLDist);

        // choose_search_class: max_l_dist == 0 is ExactSearch, whose matches are not consolidated;
        // otherwise every limit normalizes to max_l_dist, which selects LevenshteinSearch.
        if (maxLDist == 0)
            return SearchExact(sub, seq, 0, seq.Length).Select(i => new NearMatch(i, i + sub.Length, 0)).ToList();

        IEnumerable<NearMatch> matches = sub.Length / (maxLDist + 1) >= 3
            ? NgramSearch(sub, seq, maxLDist)
            : LinearProgrammingSearch(sub, seq, maxLDist);
        return Consolidate(matches);
    }

    // search_exact over a str: str.find(subsequence, start, end) repeatedly, overlaps allowed.
    private static IEnumerable<int> SearchExact(int[] sub, int[] seq, int startIndex, int endIndex)
    {
        startIndex = Math.Clamp(startIndex, 0, seq.Length);
        endIndex = Math.Clamp(endIndex, startIndex, seq.Length);
        for (int i = startIndex; i + sub.Length <= endIndex; i++)
            if (seq.AsSpan(i, sub.Length).SequenceEqual(sub))
                yield return i;
    }

    // find_near_matches_levenshtein_linear_programming.
    private static IEnumerable<NearMatch> LinearProgrammingSearch(int[] sub, int[] seq, int maxLDist)
    {
        int subLen = sub.Length;
        if (maxLDist >= subLen)
        {
            for (int index = 0; index <= seq.Length; index++)
                yield return new NearMatch(index, index, subLen);
            yield break;
        }

        // make_char2first_subseq_index: the first index of each char within sub[:max_l_dist + 1].
        Dictionary<int, int> firstIndex = [];
        for (int i = Math.Min(maxLDist + 1, subLen) - 1; i >= 0; i--)
            firstIndex[sub[i]] = i;

        List<(int Start, int SubIndex, int Dist)> candidates = [];
        for (int index = 0; index < seq.Length; index++)
        {
            int ch = seq[index];
            List<(int Start, int SubIndex, int Dist)> next = [];

            if (firstIndex.TryGetValue(ch, out int idxInSub))
            {
                if (idxInSub + 1 == subLen)
                    yield return new NearMatch(index, index + 1, idxInSub);
                else
                    next.Add((index, idxInSub + 1, idxInSub));
            }

            foreach ((int start, int subIndex, int dist) in candidates)
            {
                if (sub[subIndex] == ch)
                {
                    if (subIndex + 1 == subLen)
                        yield return new NearMatch(start, index + 1, dist);
                    else
                        next.Add((start, subIndex + 1, dist));
                }
                else
                {
                    if (dist == maxLDist)
                        continue;

                    next.Add((start, subIndex, dist + 1));

                    if (index + 1 < seq.Length && subIndex + 1 < subLen)
                        next.Add((start, subIndex + 1, dist + 1));

                    for (int skipped = 1; skipped <= maxLDist - dist; skipped++)
                    {
                        if (subIndex + skipped == subLen)
                        {
                            yield return new NearMatch(start, index + 1, dist + skipped);
                            break;
                        }
                        if (sub[subIndex + skipped] == ch)
                        {
                            if (subIndex + skipped + 1 == subLen)
                                yield return new NearMatch(start, index + 1, dist + skipped);
                            else
                                next.Add((start, subIndex + 1 + skipped, dist + skipped));
                            break;
                        }
                    }
                }
            }

            candidates = next;
        }

        foreach ((int start, int subIndex, int dist) in candidates)
        {
            int total = dist + subLen - subIndex;
            if (total <= maxLDist)
                yield return new NearMatch(start, seq.Length, total);
        }
    }

    // find_near_matches_levenshtein_ngrams.
    private static IEnumerable<NearMatch> NgramSearch(int[] sub, int[] seq, int maxLDist)
    {
        int subLen = sub.Length;
        int seqLen = seq.Length;
        int ngramLen = subLen / (maxLDist + 1);

        for (int ngramStart = 0; ngramStart <= subLen - ngramLen; ngramStart += ngramLen)
        {
            int ngramEnd = ngramStart + ngramLen;
            int[] beforeReversed = sub[..ngramStart];
            Array.Reverse(beforeReversed);
            int[] after = sub[ngramEnd..];
            int startIndex = Math.Max(0, ngramStart - maxLDist);
            int endIndex = Math.Min(seqLen, seqLen - subLen + ngramEnd + maxLDist);

            foreach (int index in SearchExact(sub[ngramStart..ngramEnd], seq, startIndex, endIndex).ToList())
            {
                (int? distRight, int rightSize) = Expand(after, Slice(seq, index + ngramLen, index - ngramStart + subLen + maxLDist), maxLDist);
                if (distRight is null)
                    continue;
                int[] leftSeq = Slice(seq, Math.Max(0, index - ngramStart - (maxLDist - distRight.Value)), index);
                Array.Reverse(leftSeq);
                (int? distLeft, int leftSize) = Expand(beforeReversed, leftSeq, maxLDist - distRight.Value);
                if (distLeft is null)
                    continue;
                yield return new NearMatch(index - leftSize, index + ngramLen + rightSize, distLeft.Value + distRight.Value);
            }
        }
    }

    // Python slice seq[start:end] for non-negative bounds.
    private static int[] Slice(int[] seq, int start, int end)
    {
        start = Math.Min(start, seq.Length);
        end = Math.Min(end, seq.Length);
        return end <= start ? [] : seq[start..end];
    }

    private static (int? Dist, int Size) Expand(int[] sub, int[] seq, int maxLDist) =>
        sub.Length > Math.Max(maxLDist * 2, 10) ? ExpandLong(sub, seq, maxLDist) : ExpandShort(sub, seq, maxLDist);

    // _py_expand_short.
    private static (int? Dist, int Size) ExpandShort(int[] sub, int[] seq, int maxLDist)
    {
        int subLen = sub.Length;
        if (subLen == 0)
            return (0, 0);

        int[] scores = Enumerable.Range(1, subLen).ToArray();
        int minScore = subLen;
        int minScoreIdx = -1;

        for (int seqIndex = 0; seqIndex < seq.Length; seqIndex++)
        {
            int ch = seq[seqIndex];
            int a = seqIndex;
            int c = a + 1;
            for (int subIndex = 0; subIndex < subLen; subIndex++)
            {
                int b = scores[subIndex];
                c = scores[subIndex] = Math.Min(Math.Min(a + (ch != sub[subIndex] ? 1 : 0), b + 1), c + 1);
                a = b;
            }

            if (c <= minScore)
            {
                minScore = c;
                minScoreIdx = seqIndex;
            }
            else if (scores.Min() >= minScore)
            {
                break;
            }
        }

        return minScore <= maxLDist ? (minScore, minScoreIdx + 1) : (null, 0);
    }

    // _py_expand_long.
    private static (int? Dist, int Size) ExpandLong(int[] sub, int[] seq, int maxLDist)
    {
        int subLen = sub.Length;
        if (subLen == 0)
            return (0, 0);

        int[] scores = Enumerable.Range(1, subLen).ToArray();
        int minScore = subLen;
        int minScoreIdx = -1;
        int maxGoodScore = maxLDist;
        int? newRangeStart = 0;
        int newRangeEnd = subLen - 1;

        for (int seqIndex = 0; seqIndex < seq.Length; seqIndex++)
        {
            int ch = seq[seqIndex];
            int rangeStart = newRangeStart!.Value;
            int rangeEnd = Math.Min(subLen, newRangeEnd + 1);

            int a = seqIndex;
            int c = a + 1;

            if (c <= maxGoodScore)
            {
                newRangeStart = 0;
                newRangeEnd = 0;
            }
            else
            {
                newRangeStart = null;
                newRangeEnd = -1;
            }

            for (int subIndex = rangeStart; subIndex < rangeEnd; subIndex++)
            {
                int b = scores[subIndex];
                c = scores[subIndex] = Math.Min(Math.Min(a + (ch != sub[subIndex] ? 1 : 0), b + 1), c + 1);
                a = b;

                if (c <= maxGoodScore)
                {
                    newRangeStart ??= subIndex;
                    newRangeEnd = Math.Max(newRangeEnd, subIndex + 1 + (maxGoodScore - c));
                }
            }

            if (newRangeStart is null)
                break;

            if (rangeEnd == subLen && c <= minScore)
            {
                minScore = c;
                minScoreIdx = seqIndex;
                if (minScore < maxGoodScore)
                    maxGoodScore = minScore;
            }
        }

        return minScore <= maxLDist ? (minScore, minScoreIdx + 1) : (null, 0);
    }

    // consolidate_overlapping_matches: group overlapping matches in arrival order, keep the best
    // (smallest distance, then longest) of each group, and sort by (start, end, dist).
    private static List<NearMatch> Consolidate(IEnumerable<NearMatch> matches)
    {
        List<(int Start, int End, HashSet<NearMatch> Matches)> groups = [];
        foreach (NearMatch match in matches)
        {
            List<int> overlapping = [];
            for (int g = 0; g < groups.Count; g++)
                if (!(match.End <= groups[g].Start || match.Start >= groups[g].End))
                    overlapping.Add(g);

            if (overlapping.Count == 0)
            {
                groups.Add((match.Start, match.End, [match]));
            }
            else if (overlapping.Count == 1)
            {
                (int start, int end, HashSet<NearMatch> set) = groups[overlapping[0]];
                set.Add(match);
                groups[overlapping[0]] = (Math.Min(start, match.Start), Math.Max(end, match.End), set);
            }
            else
            {
                HashSet<NearMatch> merged = [match];
                int start = match.Start, end = match.End;
                foreach (int g in overlapping)
                    foreach (NearMatch m in groups[g].Matches)
                    {
                        merged.Add(m);
                        start = Math.Min(start, m.Start);
                        end = Math.Max(end, m.End);
                    }
                for (int k = overlapping.Count - 1; k >= 0; k--)
                    groups.RemoveAt(overlapping[k]);
                groups.Add((start, end, merged));
            }
        }

        return groups
            .Select(g => g.Matches.OrderBy(m => m.Dist).ThenByDescending(m => m.End - m.Start).ThenBy(m => m.Start).First())
            .OrderBy(m => m.Start).ThenBy(m => m.End).ThenBy(m => m.Dist)
            .ToList();
    }
}
