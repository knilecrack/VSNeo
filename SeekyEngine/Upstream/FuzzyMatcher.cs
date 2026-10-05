// SeekyVS — Visual Studio 2026 port spike for the Seeky VS Code extension.

namespace SeekyVS;

using System;
using System.Collections.Generic;

/// <summary>
/// Subsequence fuzzy matcher over short identifiers, scoring camel-hump and word-boundary hits
/// the way "shwcr" is expected to find "ShowCoreAsync".
/// </summary>
/// <remarks>
/// fff does the fuzzy matching for file and grep searches natively; symbol search cannot reuse it
/// because the candidates are names this process extracted from grep lines
/// (<see cref="SymbolClassifier"/>), not paths fff has indexed. Greedy left-to-right with a
/// boundary-preferring first hit — good enough for identifier-length strings and allocation-free
/// apart from the returned ranges.
/// </remarks>
internal static class FuzzyMatcher
{
    private const int BoundaryBonus = 12;
    private const int ConsecutiveBonus = 8;
    private const int FirstCharBonus = 10;
    private const int GapPenalty = 1;
    private const int MaxGapPenalty = 12;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>. Returns false only
    /// when the query is not a case-insensitive subsequence of the candidate. An empty query
    /// matches with score 0 and no ranges. Higher scores are better.
    /// </summary>
    internal static bool TryMatch(string query, string candidate, out int score, out (int Start, int End)[] ranges)
    {
        score = 0;
        ranges = [];
        if (query.Length == 0)
        {
            return true;
        }

        if (candidate.Length == 0 || query.Length > candidate.Length)
        {
            return false;
        }

        // Preferring word boundaries gives better ranges but is greedy, so it can step over a
        // subsequence that does exist: "targetresult" vs "TargetResult" jumps from 'Ta|r' to the
        // camel-hump 'R', skipping "rget", and then finds no 'g'. Plain left-to-right is complete
        // for subsequence detection, so a failed boundary pass retries without it — the score
        // suffers, but a real match is never reported as a miss.
        return TryMatchCore(query, candidate, preferBoundaries: true, out score, out ranges)
            || TryMatchCore(query, candidate, preferBoundaries: false, out score, out ranges);
    }

    private static bool TryMatchCore(
        string query, string candidate, bool preferBoundaries, out int score, out (int Start, int End)[] ranges)
    {
        score = 0;
        ranges = [];

        List<(int Start, int End)> spans = [];
        int candidateIndex = 0;
        int previousMatch = -2;
        int total = 0;

        for (int q = 0; q < query.Length; q++)
        {
            char wanted = char.ToLowerInvariant(query[q]);
            int found = -1;
            int fallback = -1;

            // Prefer a hit on a word boundary (start, camel hump, after _ . / -) over the first
            // available one: "sm" should land on "SeekyModal", not on the "s"+"m" inside "Seeky".
            // A hit that continues the previous one always wins — that is what keeps runs intact.
            bool consecutive = candidateIndex == previousMatch + 1
                && candidateIndex < candidate.Length
                && char.ToLowerInvariant(candidate[candidateIndex]) == wanted;
            if (preferBoundaries && !consecutive)
            {
                for (int c = candidateIndex; c < candidate.Length; c++)
                {
                    if (char.ToLowerInvariant(candidate[c]) != wanted)
                    {
                        continue;
                    }

                    if (fallback < 0)
                    {
                        fallback = c;
                    }

                    if (IsBoundary(candidate, c))
                    {
                        found = c;
                        break;
                    }
                }
            }

            if (found < 0 && fallback < 0)
            {
                for (int c = candidateIndex; c < candidate.Length; c++)
                {
                    if (char.ToLowerInvariant(candidate[c]) == wanted)
                    {
                        fallback = c;
                        break;
                    }
                }
            }

            if (found < 0)
            {
                found = fallback;
            }

            if (found < 0)
            {
                return false;
            }

            int gain = 1;
            if (found == 0)
            {
                gain += FirstCharBonus;
            }

            if (IsBoundary(candidate, found))
            {
                gain += BoundaryBonus;
            }

            if (found == previousMatch + 1)
            {
                gain += ConsecutiveBonus;
                var last = spans[^1];
                spans[^1] = (last.Start, found + 1);
            }
            else
            {
                int gap = previousMatch < 0 ? found : found - previousMatch - 1;
                gain -= Math.Min(gap * GapPenalty, MaxGapPenalty);
                spans.Add((found, found + 1));
            }

            total += gain;
            previousMatch = found;
            candidateIndex = found + 1;
        }

        // Prefer tight matches over long names that merely contain the letters.
        total -= candidate.Length / 4;
        if (candidate.Length == query.Length)
        {
            total += 20;
        }

        score = total;
        ranges = [.. spans];
        return true;
    }

    private static bool IsBoundary(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        char current = text[index];
        char previous = text[index - 1];
        if (previous is '_' or '.' or '/' or '\\' or '-' or ' ')
        {
            return true;
        }

        return char.IsUpper(current) && !char.IsUpper(previous);
    }
}
