using System;

namespace VSNeo_Extension.Nvim;

/// <summary>One run of nvim's evaluated statusline: text and its resolved colors (0xRRGGBB, -1 unset).</summary>
public readonly struct StatusSegment
{
    public StatusSegment(string text, int fg, int bg, bool bold)
    {
        Text = text;
        Fg = fg;
        Bg = bg;
        Bold = bold;
    }

    public string Text { get; }
    public int Fg { get; }
    public int Bg { get; }
    public bool Bold { get; }
}

/// <summary>The statusline as nvim last rendered it; replaced whole, never mutated.</summary>
public sealed class StatusLineSnapshot
{
    public static readonly StatusLineSnapshot Off = new(0, -1, -1, Array.Empty<StatusSegment>());

    public StatusLineSnapshot(int lastStatus, int fg, int bg, StatusSegment[] segments)
    {
        LastStatus = lastStatus;
        Fg = fg;
        Bg = bg;
        Segments = segments;
    }

    /// <summary>The 'laststatus' the user asked for; nvim's own stays 0.</summary>
    public int LastStatus { get; }

    /// <summary>The StatusLine group's colors, for the part no segment covers.</summary>
    public int Fg { get; }
    public int Bg { get; }
    public StatusSegment[] Segments { get; }
}

internal sealed partial class NvimStateHub
{
    /// <summary>Read from any thread: one reference, swapped whole.</summary>
    public StatusLineSnapshot StatusLine { get; private set; } = StatusLineSnapshot.Off;

    /// <summary>Raised on the RPC read thread.</summary>
    public event Action StatusLineChanged = null!;

    /// <summary>
    /// vsneo_statusline is [laststatus, fg, bg, segments], each segment
    /// {text, fg, bg, bold}; the companion sends it only on a change
    /// (075-statusline.lua).
    /// </summary>
    private void HandleStatusLine(object[] args)
    {
        if (args == null || args.Length < 4)
        {
            return;
        }

        var items = args[3] as object[] ?? Array.Empty<object>();
        var segments = new StatusSegment[items.Length];
        int count = 0;
        foreach (var item in items)
        {
            if (item is object[] seg && seg.Length >= 4)
            {
                segments[count++] = new StatusSegment(
                    AsString(seg[0]) ?? string.Empty, ToInt(seg[1]), ToInt(seg[2]), ToInt(seg[3]) == 1);
            }
        }

        Array.Resize(ref segments, count);
        StatusLine = new StatusLineSnapshot(ToInt(args[0]), ToInt(args[1]), ToInt(args[2]), segments);
        StatusLineChanged?.Invoke();
    }
}
