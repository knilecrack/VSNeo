using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Nvim;

// Jump-label overlays: whether one is up, and its labels.
internal sealed partial class NvimStateHub
{
    /// <summary>
    /// An overlay interaction (jump labels, anything Lua drives) is
    /// collecting keys in nvim. While set, the command filter routes the
    /// keys Visual Studio turns into commands - Escape, Enter, Backspace,
    /// arrows - to nvim, exactly as in CmdLine mode. Read on the key path;
    /// there is deliberately no event.
    /// </summary>
    public bool OverlayActive { get; private set; }

    /// <summary>
    /// The labels the active overlay wants drawn, in nvim coordinates.
    /// Empty when none are active.
    /// </summary>
    public IReadOnlyList<OverlayLabel> OverlayLabels { get; private set; }
        = Array.Empty<OverlayLabel>();

    public event Action OverlayLabelsChanged = null!;

    /// <summary>
    /// vsneo_overlay_active opens or closes an overlay interaction. Closing
    /// drops any labels with it, so a driver that crashes mid-interaction
    /// cannot leave paint behind.
    /// </summary>
    private void HandleOverlayActive(object[] args)
    {
        OverlayActive = args != null && args.Length > 0 && ToInt(args[0]) != 0;
        if (!OverlayActive && OverlayLabels.Count != 0)
        {
            OverlayLabels = Array.Empty<OverlayLabel>();
            OverlayLabelsChanged?.Invoke();
        }
    }

    /// <summary>
    /// vsneo_overlay_labels carries one array of [line, startByte, endByte,
    /// text] entries, the same coordinate convention as search matches.
    /// </summary>
    private void HandleOverlayLabels(object[] args)
    {
        var items = args != null && args.Length > 0 ? args[0] as object[] : null;
        if (items == null)
        {
            if (OverlayLabels.Count != 0)
            {
                OverlayLabels = Array.Empty<OverlayLabel>();
                OverlayLabelsChanged?.Invoke();
            }

            return;
        }

        var labels = new List<OverlayLabel>(items.Length);
        foreach (var item in items)
        {
            if (item is object[] m && m.Length >= 4)
            {
                labels.Add(new OverlayLabel(
                    ToInt(m[0]), ToInt(m[1]), ToInt(m[2]), AsString(m[3]) ?? string.Empty));
            }
        }

        OverlayLabels = labels;
        OverlayLabelsChanged?.Invoke();
    }
}
