using System;

namespace VSNeo_Extension.Nvim;

// Folds, and the number/scrolloff settings the margins read.
internal sealed partial class NvimStateHub
{
    /// <summary>
    /// nvim's actual fold set changed (a z-command, or a 'foldopen'
    /// auto-open): flat [start, end, closed] triples - 1-based lines,
    /// closed as 0/1 - covering every fold that still EXISTS, as detected
    /// by the companion polling foldlevel()/foldclosed() on every state
    /// push. A fold absent from the list was deleted (zd);
    /// FoldSynchronizer reconciles Visual Studio's outlining from it.
    /// </summary>
    public event Action<int[]> FoldsChanged = null!;

    /// <summary>
    /// zf in nvim: create a user fold over [start, end] (1-based lines) in
    /// the document at path. The fold is made Visual Studio-side (a real
    /// outlining region) and round-trips back into nvim.
    /// </summary>
    public event Action<string, int, int> FoldCreateRequested = null!;

    /// <summary>
    /// vsneo_folds_changed is [flat]: a single array of [start, end, closed]
    /// triples, 1-based lines, closed 0/1 - the companion's actual fold set
    /// (existing folds only; a deleted fold is simply absent). No caching -
    /// FoldSynchronizer reconciles against Visual Studio's outlining on
    /// arrival, which is the comparison that matters.
    /// </summary>
    private void HandleFoldsChanged(object[] args)
    {
        if (args.Length == 0 || !(args[0] is object[] flat))
        {
            return;
        }

        var triples = new int[flat.Length];
        for (int i = 0; i < flat.Length; i++)
        {
            triples[i] = ToInt(flat[i]);
        }

        FoldsChanged?.Invoke(triples);
    }

    /// <summary>vsneo_fold_create is [path, start, end]: the zf range.</summary>
    private void HandleFoldCreate(object[] args)
    {
        if (args.Length < 3)
        {
            return;
        }

        var path = AsString(args[0]);
        if (path.Length == 0)
        {
            return;
        }

        FoldCreateRequested?.Invoke(path, ToInt(args[1]), ToInt(args[2]));
    }

    /// <summary>
    /// nvim's 'number' and 'relativenumber' window options, as the companion
    /// last pushed them. nvim renders nothing itself; the relative line
    /// number margin is the only consumer, and the Tools &gt; Options
    /// override can mute even that.
    /// </summary>
    public bool NvimNumber { get; private set; }
    public bool NvimRelativeNumber { get; private set; }
    public event Action LineNumbersChanged = null!;

    /// <summary>The effective window-local vertical scroll padding.</summary>
    public int Scrolloff { get; private set; }

    /// <summary>
    /// vsneo_linenumbers is [number, relativenumber], 0/1 each, pushed after
    /// the rc and on every OptionSet for either option.
    /// </summary>
    private void HandleLineNumbers(object[] args)
    {
        if (args == null || args.Length < 2)
        {
            return;
        }

        bool number = ToInt(args[0]) != 0;
        bool relative = ToInt(args[1]) != 0;
        if (number == NvimNumber && relative == NvimRelativeNumber)
        {
            return;
        }

        NvimNumber = number;
        NvimRelativeNumber = relative;
        LineNumbersChanged?.Invoke();
    }
}
