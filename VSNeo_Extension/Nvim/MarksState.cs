using System;
using System.Collections.Generic;

namespace VSNeo_Extension.Nvim;

/// <summary>
/// nvim's marks per file, for the scrollbar (ScrollbarMarkTagger).
///
/// Split out of NvimStateHub so the path normalization and the "replaced
/// wholesale per file" rule can be tested without the hub. The dictionary is
/// keyed by the normalized path, case-insensitively; readers take a snapshot
/// reference under the lock.
/// </summary>
internal sealed class MarksState
{
    private readonly Dictionary<string, IReadOnlyList<BufferMark>> _marks =
        new Dictionary<string, IReadOnlyList<BufferMark>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<BufferMark> MarksFor(string? path)
    {
        if (path == null)
        {
            return Array.Empty<BufferMark>();
        }

        lock (_marks)
        {
            return _marks.TryGetValue(path, out IReadOnlyList<BufferMark>? list)
                ? list
                : Array.Empty<BufferMark>();
        }
    }

    /// <summary>A file's marks changed; the argument is its normalized path.</summary>
    public event Action<string>? MarksChanged;

    /// <summary>
    /// vsneo_marks is [path, [[line, name], ...]] - a-z of that buffer and A-Z
    /// pointing into it, 0-based lines. Paths are normalized like
    /// vsneo_buf_enter's, so they compare against the hub's CurrentBufferPath.
    /// </summary>
    public void HandleMarks(object[] args)
    {
        if (args == null || args.Length < 2)
        {
            return;
        }

        string? path = NvimStateHub.NormalizePath(NvimStateHub.AsString(args[0]));
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string normalized = path!;

        var list = new List<BufferMark>();
        if (args[1] is object[] items)
        {
            foreach (var item in items)
            {
                if (item is object[] mark && mark.Length > 1)
                {
                    list.Add(new BufferMark(
                        NvimStateHub.ToInt(mark[0]), NvimStateHub.AsString(mark[1]) ?? string.Empty));
                }
            }
        }

        lock (_marks)
        {
            _marks[normalized] = list;
        }

        MarksChanged?.Invoke(normalized);
    }
}
