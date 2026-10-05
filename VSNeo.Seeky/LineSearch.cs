// Seeky picker embedded in NeoVS — the Current File search (the page's 'lines' mode). Not in
// upstream SeekyVS: a host needs the active editor's text to offer it, which the in-proc
// picker has for free (see SeekyPickerController.EditorSnapshot).

namespace VSNeo.Seeky;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>
/// Searches one document's lines the way Live Grep searches the workspace, under the same
/// three sub-modes. Pure — no Visual Studio types — so the unit tests run it directly.
/// </summary>
/// <remarks>
/// <para>
/// Plain and regex are smart-case, as Vim's <c>'smartcase'</c>: case-insensitive unless the
/// query has an uppercase letter. Fuzzy matches each whitespace-separated word on its own
/// (fzf's extended mode): one subsequence over a whole source line matches nearly every
/// line, while "task run" finding lines with both words is what a person means.
/// </para>
/// <para>
/// Order: fuzzy by score; ties, and every plain/regex hit, in Vim's <c>/</c> order — the first
/// match below the caret line, wrapping to the top. The page shows index 0 next to the prompt,
/// preselected, so Enter with no navigation behaves like <c>/pattern</c> then Enter.
/// </para>
/// </remarks>
internal static class LineSearch
{
    /// <summary>
    /// Longest line considered, in chars. Minified files carry lines of megabytes; the picker
    /// row could not show them, and the fuzzy matcher is not meant for them.
    /// </summary>
    internal const int MaxLineLength = 1000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>A matching line: 1-based line number, text, and highlight spans into it.</summary>
    internal readonly record struct Hit(int Line, string Text, (int Start, int End)[] Ranges, int Score);

    /// <summary>
    /// The lines of <paramref name="lines"/> matching <paramref name="query"/>, best first, at most
    /// <paramref name="maxResults"/>. <paramref name="caretLine"/> is 1-based.
    /// <paramref name="regexError"/> is set when a regex query did not compile and the search fell
    /// back to a literal one (Live Grep reports the same fallback).
    /// </summary>
    internal static List<Hit> Search(
        IReadOnlyList<string> lines,
        string query,
        string grepMode,
        int caretLine,
        int maxResults,
        out string? regexError,
        CancellationToken cancellationToken = default)
    {
        regexError = null;
        var hits = new List<Hit>();
        if (string.IsNullOrWhiteSpace(query) || lines.Count == 0 || maxResults <= 0)
        {
            return hits;
        }

        Func<string, (bool Matched, (int Start, int End)[] Ranges, int Score)> match;
        if (grepMode == "fuzzy")
        {
            string[] words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            match = text => MatchWords(words, text);
        }
        else
        {
            Regex regex;
            RegexOptions options = HasUpper(query) ? RegexOptions.None : RegexOptions.IgnoreCase;
            try
            {
                regex = new Regex(grepMode == "regex" ? query : Regex.Escape(query), options, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                regexError = ex.Message;
                regex = new Regex(Regex.Escape(query), options, RegexTimeout);
            }

            match = text => MatchRegex(regex, text);
        }

        // The file from the line below the caret to the end, then from the top: '/' order.
        int start = caretLine < 1 || caretLine >= lines.Count ? 0 : caretLine;
        for (int n = 0; n < lines.Count; n++)
        {
            if ((n & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int index = (start + n) % lines.Count;
            string text = lines[index];
            if (text.Length > MaxLineLength)
            {
                text = text.Substring(0, MaxLineLength);
            }

            if (text.Trim().Length == 0)
            {
                continue;
            }

            var (matched, ranges, score) = match(text);
            if (matched)
            {
                hits.Add(new Hit(index + 1, text, ranges, score));
            }
        }

        if (grepMode == "fuzzy")
        {
            // OrderBy, not List.Sort: it is stable, so equal scores keep '/' order.
            hits = hits.OrderByDescending(h => h.Score).ToList();
        }

        if (hits.Count > maxResults)
        {
            hits.RemoveRange(maxResults, hits.Count - maxResults);
        }

        return hits;
    }

    private static (bool, (int Start, int End)[], int) MatchWords(string[] words, string text)
    {
        int total = 0;
        var ranges = new List<(int Start, int End)>();
        foreach (string word in words)
        {
            if (!FuzzyMatcher.TryMatch(word, text, out int score, out (int Start, int End)[] wordRanges))
            {
                return (false, [], 0);
            }

            total += score;
            ranges.AddRange(wordRanges);
        }

        return (true, ranges.ToArray(), total);
    }

    private static (bool, (int Start, int End)[], int) MatchRegex(Regex regex, string text)
    {
        try
        {
            bool matched = false;
            var ranges = new List<(int Start, int End)>();
            for (Match m = regex.Match(text); m.Success; m = m.NextMatch())
            {
                // Zero-width matches (^, \b, lookarounds) select the line but highlight nothing.
                matched = true;
                if (m.Length > 0)
                {
                    ranges.Add((m.Index, m.Index + m.Length));
                }
            }

            return (matched, ranges.ToArray(), 0);
        }
        catch (RegexMatchTimeoutException)
        {
            // A catastrophic pattern costs one line, not the search.
            return (false, [], 0);
        }
    }

    private static bool HasUpper(string query)
    {
        foreach (char c in query)
        {
            if (char.IsUpper(c))
            {
                return true;
            }
        }

        return false;
    }
}
