using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace VSNeo_Extension.Nvim;

// The pushed keymaps and insert mappings, and the recording badge.
internal sealed partial class NvimStateHub
{
    private readonly KeymapCatalog _keymaps = new();

    /// <summary>Insert-mapping lookup for the key path; see KeymapCatalog.</summary>
    public bool IsInsertMapped(string keys) => _keymaps.IsInsertMapped(keys);

    /// <summary>The mapping table the companion pushed, per mode.</summary>
    public IReadOnlyList<KeymapEntry> KeymapsFor(VimMode mode) => _keymaps.KeymapsFor(mode);

    /// <summary>Mappings whose lhs strictly extends the typed prefix.</summary>
    public IReadOnlyList<KeymapEntry> KeymapChildren(VimMode mode, string prefix) =>
        _keymaps.KeymapChildren(mode, prefix);

    /// <summary>Allocation-free "is there a hint?" for the key path.</summary>
    public bool HasKeymapChildren(VimMode mode, string prefix) =>
        _keymaps.HasKeymapChildren(mode, prefix);

    /// <summary>
    /// The register a macro is being recorded into ("q", "a", ...), or
    /// null when not recording. Driven by RecordingEnter/RecordingLeave,
    /// which bracket the recording exactly - msg_showmode never carries it.
    /// </summary>
    public string RecordingReg { get; private set; } = null!;

    public event Action RecordingChanged = null!;

    /// <summary>
    /// vsneo_keymaps is [mode, items]: "n" or "x", and one [lhs, desc]
    /// pair per mapping. No event is raised - the only reader (the
    /// which-key popup) pulls from the key path, and hints are never
    /// shown unprompted.
    /// </summary>
    private void HandleKeymaps(object[] args)
    {
        if (args == null || args.Length < 2 || !(args[1] is object[] items))
        {
            return;
        }

        var entries = new List<KeymapEntry>(items.Length);
        foreach (var item in items)
        {
            if (item is object[] pair && pair.Length >= 2)
            {
                var lhs = AsString(pair[0]);
                var desc = AsString(pair[1]);
                if (!string.IsNullOrEmpty(lhs))
                {
                    entries.Add(new KeymapEntry(lhs, desc ?? string.Empty));
                }
            }
        }

        var mode = AsString(args[0]);
        if (mode == "n")
        {
            _keymaps.Replace(VimMode.Normal, entries);
        }
        else if (mode == "x" || mode == "v")
        {
            _keymaps.Replace(VimMode.Visual, entries);
        }
    }

    /// <summary>
    /// vsneo_imaps is [items]: the lhs tokens of the user's own
    /// insert-mode mappings on named keys. No rhs crosses the wire - the
    /// key path feeds the lhs back through nvim_input and nvim runs the
    /// mapping itself, so string, Lua-callback and expr rhs all work.
    /// </summary>
    private void HandleImaps(object[] args)
    {
        if (args == null || args.Length < 1 || !(args[0] is object[] items))
        {
            return;
        }

        var keys = new List<string>(items.Length);
        foreach (var item in items)
        {
            var lhs = AsString(item);
            if (!string.IsNullOrEmpty(lhs))
            {
                keys.Add(lhs);
            }
        }

        _keymaps.SetInsert(keys);
    }

    /// <summary>
    /// vsneo_recording is [register]: the register being recorded into, or
    /// "" when RecordingLeave fired (the companion pushes "" explicitly;
    /// reg_recording() itself is not yet cleared when the event fires).
    /// </summary>
    private void HandleRecording(object[] args)
    {
        // Explicit null check, not IsNullOrEmpty: net472's reference
        // assemblies carry no NotNullWhen annotation to prove the flow.
        var raw = args != null && args.Length > 0 ? AsString(args[0]) : null;
        string value;
        if (raw == null || raw.Length == 0)
        {
            value = null!;
        }
        else
        {
            value = raw;
        }

        if (RecordingReg == value)
        {
            return;
        }

        RecordingReg = value;
        RecordingChanged?.Invoke();
    }
}
