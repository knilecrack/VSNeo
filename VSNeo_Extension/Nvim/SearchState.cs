using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Nvim;

/// <summary>
/// hlsearch state: the match list the adornment and scrollbar draw, the
/// whole-buffer count for the [n/N] chip, the highlight colors, and the yank
/// flash segments.
///
/// Split out of NvimStateHub so the list comparison and the count bookkeeping
/// can be tested without the RPC hub. Everything runs on the RPC read thread;
/// readers see a whole list reference swapped at once.
/// </summary>
internal sealed class SearchState
{
    /// <summary>
    /// The current hlsearch matches for nvim's current buffer, in nvim
    /// coordinates. Empty when hlsearch is off or there are no matches.
    /// </summary>
    public IReadOnlyList<SearchMatch> Matches { get; private set; } = Array.Empty<SearchMatch>();

    /// <summary>
    /// Whole-buffer figures for the [n/N] chip, from searchcount() in the
    /// companion: how many matches precede the scanned range (so the index
    /// within <see cref="Matches"/> becomes an index in the buffer), the
    /// total, and whether the total is a floor (the walk timed out).
    /// </summary>
    public int MatchesBefore { get; private set; }
    public int Total { get; private set; } = -1;
    public bool TotalIncomplete { get; private set; }

    /// <summary>Whether the [current/total] chip is drawn. On by default.</summary>
    public bool CountEnabled { get; private set; } = true;

    /// <summary>
    /// Colors the adornments draw with, straight from nvim's highlight
    /// groups: Search, CurSearch (or IncSearch), IncSearch. -1 when the group
    /// has no background; the adornments fall back to their defaults.
    /// </summary>
    public int SearchColor { get; private set; } = -1;
    public int CurrentMatchColor { get; private set; } = -1;
    public int YankColor { get; private set; } = -1;

    public event Action? MatchesChanged;
    public event Action? HighlightsChanged;

    /// <summary>
    /// Something was yanked: [line, startByte, endByte] triples in nvim
    /// coordinates. Fire-and-forget; the adornment owns how long the flash
    /// stays up.
    /// </summary>
    public event Action<IReadOnlyList<SearchMatch>>? YankFlashed;

    public void HandleSearchCount(object[] args)
    {
        if (args == null || args.Length < 3)
        {
            return;
        }

        MatchesBefore = Math.Max(0, NvimStateHub.ToInt(args[0]));
        Total = Math.Max(0, NvimStateHub.ToInt(args[1]));
        TotalIncomplete = NvimStateHub.ToInt(args[2]) != 0;
    }

    /// <summary>
    /// vsneo_search_matches carries the matches computed by the Lua companion:
    /// one array of [line, startByte, endByte] triples.
    /// </summary>
    public void HandleSearchMatches(object[] args)
    {
        if (args == null || args.Length == 0 || !(args[0] is object[] items))
        {
            MatchesBefore = 0;
            Total = -1;
            TotalIncomplete = false;
            if (Matches.Count != 0)
            {
                Matches = Array.Empty<SearchMatch>();
                MatchesChanged?.Invoke();
            }

            return;
        }

        var matches = new List<SearchMatch>(items.Length);
        foreach (var item in items)
        {
            if (item is object[] m && m.Length >= 3)
            {
                matches.Add(new SearchMatch(
                    NvimStateHub.ToInt(m[0]), NvimStateHub.ToInt(m[1]), NvimStateHub.ToInt(m[2])));
            }
        }

        // The Lua side resends the full list after every edit even when the
        // matches did not move (typing elsewhere in the buffer); re-firing
        // would rebuild the adornment for nothing.
        IReadOnlyList<SearchMatch> current = Matches;
        if (current.Count == matches.Count)
        {
            bool same = true;
            for (int i = 0; i < matches.Count; i++)
            {
                SearchMatch a = current[i];
                SearchMatch b = matches[i];
                if (a.Line != b.Line || a.StartByte != b.StartByte || a.EndByte != b.EndByte)
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        Matches = matches;
        MatchesChanged?.Invoke();
    }

    /// <summary>
    /// vsneo_highlights carries three positional colors from the companion:
    /// Search, CurSearch (or IncSearch), IncSearch - 0xRRGGBB, or -1 when the
    /// group has no background and the adornment keeps its default.
    /// </summary>
    public void HandleHighlights(object[] args)
    {
        if (args == null || args.Length < 3)
        {
            return;
        }

        SearchColor = NvimStateHub.ToInt(args[0]);
        CurrentMatchColor = NvimStateHub.ToInt(args[1]);
        YankColor = NvimStateHub.ToInt(args[2]);
        // Optional tail: an older companion sends three values, and the
        // counter stays on.
        CountEnabled = args.Length < 4 || NvimStateHub.ToInt(args[3]) != 0;
        HighlightsChanged?.Invoke();
    }

    /// <summary>
    /// vsneo_yank carries the yanked region as [line, startByte, endByte]
    /// triples, the same shape as search matches. Purely an event: nothing
    /// here caches it, the flash is transient by definition.
    /// </summary>
    public void HandleYank(object[] args)
    {
        if (args == null || args.Length == 0 || !(args[0] is object[] items))
        {
            return;
        }

        var segments = new List<SearchMatch>(items.Length);
        foreach (var item in items)
        {
            if (item is object[] m && m.Length >= 3)
            {
                segments.Add(new SearchMatch(
                    NvimStateHub.ToInt(m[0]), NvimStateHub.ToInt(m[1]), NvimStateHub.ToInt(m[2])));
            }
        }

        if (segments.Count > 0)
        {
            YankFlashed?.Invoke(segments);
        }
    }
}
