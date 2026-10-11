namespace SeekyVS;

using System;
using System.Collections.Generic;

/// <summary>
/// Caps the line text an engine reply carries. One line of a minified bundle can be megabytes
/// and a reply carries a hundred of them; the page shows a line of text, not the file. A
/// highlight beyond the cut is dropped, so a jump to it falls back to the first non-blank.
/// </summary>
internal static class TextClip
{
    internal const int MaxChars = 4000;

    internal static string Text(string text, int max = MaxChars)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // A cut between the halves of a surrogate pair leaves an unpaired one, which
        // Utf8JsonWriter rejects - one emoji at the boundary would fail the whole reply.
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text.Substring(0, cut);
    }

    internal static SeekyRange[] Ranges(SeekyRange[] ranges, int length)
    {
        var kept = new List<SeekyRange>(ranges.Length);
        foreach (SeekyRange r in ranges)
        {
            if (r.Start < length)
            {
                kept.Add(new SeekyRange(r.Start, Math.Min(r.End, length)));
            }
        }

        return kept.ToArray();
    }
}
