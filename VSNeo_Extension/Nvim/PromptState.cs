using System;
using System.Collections.Generic;
using System.Text;

namespace VSNeo_Extension.Nvim;

/// <summary>
/// The redraw state that is not the document: the ext_cmdline, ext_messages,
/// ext_popupmenu and ext_hlsearch prompt surfaces, plus the pager.
///
/// Split out of NvimStateHub so the protocol parsing and the pager/message
/// rules can be exercised without an RPC client. The hub forwards its public
/// members here, so consumers (the cmdline overlay, the message margin, the
/// pager, the key path's ShowCmd read) are unchanged.
///
/// Everything here runs on the RPC read thread, except the pager's Close and
/// the completion snapshots the UI reads; the pager swaps under a gate.
/// </summary>
internal sealed class PromptState
{
    private readonly Action? _cmdLineVisibilityChanged;
    private readonly object _pagerGate = new object();

    public PromptState(Action? cmdLineVisibilityChanged = null)
    {
        _cmdLineVisibilityChanged = cmdLineVisibilityChanged;
    }

    /// <summary>Current ext_cmdline content, or null when no command line is open.</summary>
    public string CmdLine { get; private set; } = null!;

    /// <summary>The ":" "/" or "?" that opened the command line.</summary>
    public string CmdLinePrefix { get; private set; } = string.Empty;

    /// <summary>Cursor position within <see cref="CmdLine"/>, as a UTF-8 byte offset.</summary>
    public int CmdLinePos { get; private set; }

    /// <summary>Current ext_messages content, or null when no message is being shown.</summary>
    public string Message { get; private set; } = null!;

    /// <summary>The kind of message nvim reported ("", "error", "warning", etc.).</summary>
    public string MessageKind { get; private set; } = null!;

    /// <summary>Current ext_messages mode text ("-- INSERT --"), or null when inactive.</summary>
    public string ModeMessage { get; private set; } = null!;

    /// <summary>
    /// Current ext_messages showcmd text, or null when no partial command is
    /// pending. Read on the key path; a field read through the hub.
    /// </summary>
    public string ShowCmd { get; private set; } = null!;

    /// <summary>
    /// Output too long for the one-line message area - :map, :set all, :ls,
    /// :messages - or null when the pager is closed. Vim shows such output
    /// until it is dismissed (the "more" and hit-enter prompts), and with
    /// ext_messages nvim leaves that to the UI: no msg_clear ever follows a
    /// list_cmd. So only <see cref="ClosePager"/> ends it, never msg_clear.
    /// </summary>
    public string? PagerText { get; private set; }

    /// <summary>Wildmenu items for the open command line; empty when none.</summary>
    public IReadOnlyList<string> CompletionWords { get; private set; } = Array.Empty<string>();

    /// <summary>Index into <see cref="CompletionWords"/>, -1 when nothing is selected.</summary>
    public int CompletionSelected { get; private set; } = -1;

    /// <summary>
    /// Messages longer than this many lines go to the pager. The message
    /// margin sits below the text and grows to fit what it shows, so a
    /// 300-line :map filled the editor with a pane nothing could close.
    /// </summary>
    public const int MaxInlineMessageLines = 3;

    /// <summary>
    /// The 'cmdheight' the user asked for. nvim's own stays 0: every row it
    /// took would be a row its window lacks next to Visual Studio's view.
    /// Messages show in the status bar, so all it still decides is how long
    /// a message may be before it goes to the pager, as in Vim.
    /// </summary>
    public int CmdHeight { get; private set; }

    /// <summary>vsneo_cmdheight. Called on the RPC read thread.</summary>
    public void SetCmdHeight(int rows) => CmdHeight = Math.Max(0, rows);

    /// <summary>Longer messages go to the pager: Vim's more prompt is past 'cmdheight'.</summary>
    public int InlineMessageLimit => Math.Max(MaxInlineMessageLines, CmdHeight);

    public event Action<string>? CmdLineChanged;
    public event Action<string>? MessageChanged;
    public event Action<string>? ModeMessageChanged;
    public event Action<string>? ShowCmdChanged;
    public event Action? CompletionsChanged;
    public event Action<string?>? PagerChanged;

    /// <summary>Close the pager (its q, Escape, Enter or close button). Any thread.</summary>
    public void ClosePager()
    {
        // Closed from the UI thread, opened and appended to from the read
        // thread: the swap is under a lock so a q racing new :messages
        // output neither loses the output nor resurrects the closed text.
        lock (_pagerGate)
        {
            if (PagerText == null)
            {
                return;
            }

            PagerText = null;
        }

        PagerChanged?.Invoke(null);
    }

    /// <summary>
    /// Consumes one redraw notification: a list of [name, event, event, ...]
    /// batches. Everything the redraw stream carries here is a prompt surface
    /// (cmdline, popupmenu, messages); the document itself arrives through
    /// the companion's own notifications.
    /// </summary>
    public void HandleRedraw(object[] args)
    {
        foreach (var batchObj in args)
        {
            if (!(batchObj is object[] batch) || batch.Length == 0)
            {
                continue;
            }

            var name = NvimStateHub.AsString(batch[0]);

            for (int i = 1; i < batch.Length; i++)
            {
                if (!(batch[i] is object[] evt))
                {
                    continue;
                }

                switch (name)
                {
                    case "cmdline_show":
                        HandleCmdlineShow(evt);
                        break;
                    case "cmdline_pos":
                        HandleCmdlinePos(evt);
                        break;
                    // null is the "no command line open" state CmdLine documents.
                    case "cmdline_hide":
                        SetCmdLine(null!);
                        break;
                    case "popupmenu_show":
                        HandlePopupmenuShow(evt);
                        break;
                    case "popupmenu_select":
                        HandlePopupmenuSelect(evt);
                        break;
                    case "popupmenu_hide":
                        HandlePopupmenuHide();
                        break;
                    case "msg_show":
                        HandleMsgShow(evt);
                        break;
                    case "msg_showmode":
                        HandleMsgShowMode(evt);
                        break;
                    case "msg_showcmd":
                        HandleMsgShowCmd(evt);
                        break;
                    case "msg_clear":
                        ClearMessages();
                        break;
                    case "msg_history_show":
                        HandleMsgHistoryShow(evt);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// cmdline_show is [content, pos, firstc, prompt, indent, level].
    ///
    /// The prompt character comes separately from the content, and it is not
    /// decoration: ":" and "/" and "?" are the same mechanism, so assuming ":"
    /// would show a search as though it were a command.
    /// </summary>
    private void HandleCmdlineShow(object[] evt)
    {
        // content is an array of [attr, text] chunks
        if (evt.Length == 0 || !(evt[0] is object[] chunks))
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var c in chunks)
        {
            if (c is object[] chunk && chunk.Length > 1)
            {
                sb.Append(NvimStateHub.AsString(chunk[1]));
            }
        }

        // A UTF-8 byte offset into the content, like every other column nvim
        // reports. Run it through ColumnMapper before indexing a .NET string.
        CmdLinePos = evt.Length > 1 ? Math.Max(0, NvimStateHub.ToInt(evt[1])) : 0;

        var firstc = evt.Length > 2 ? NvimStateHub.AsString(evt[2]) : null;

        // A ":" prompt sends ":" here; an input() prompt sends an empty string
        // and puts its text in the "prompt" field instead.
        CmdLinePrefix = string.IsNullOrEmpty(firstc)
            ? (evt.Length > 3 ? NvimStateHub.AsString(evt[3]) ?? string.Empty : string.Empty)
            // IsNullOrEmpty(false) guarantees non-null, but net472's reference
            // assemblies carry no NotNullWhen annotation to prove it.
            : firstc!;

        SetCmdLine(sb.ToString());
    }

    /// <summary>
    /// cmdline_pos is [pos, level]: the cursor moved inside the command line
    /// without the content changing, which is all Left, Right, &lt;C-b&gt; and
    /// &lt;C-e&gt; do.
    /// </summary>
    private void HandleCmdlinePos(object[] evt)
    {
        if (evt.Length == 0)
        {
            return;
        }

        int pos = NvimStateHub.ToInt(evt[0]);
        if (pos < 0)
        {
            return;
        }

        CmdLinePos = pos;
        CmdLineChanged?.Invoke(CmdLine);
    }

    private void SetCmdLine(string value)
    {
        CmdLine = value;
        // Opening or closing the command line can change the effective mode
        // on its own, before the companion's push catches up.
        _cmdLineVisibilityChanged?.Invoke();
        CmdLineChanged?.Invoke(value);
    }

    /// <summary>popupmenu_show is [items, selected, row, col, grid]; items are [word, kind, menu, info].</summary>
    private void HandlePopupmenuShow(object[] evt)
    {
        if (evt.Length == 0 || !(evt[0] is object[] items))
        {
            CompletionWords = Array.Empty<string>();
            CompletionSelected = -1;
            CompletionsChanged?.Invoke();
            return;
        }

        var words = new List<string>(items.Length);
        foreach (var item in items)
        {
            if (item is object[] entry && entry.Length > 0)
            {
                words.Add(NvimStateHub.AsString(entry[0]) ?? string.Empty);
            }
        }

        CompletionWords = words;
        CompletionSelected = evt.Length > 1 ? NvimStateHub.ToInt(evt[1]) : -1;
        CompletionsChanged?.Invoke();
    }

    /// <summary>popupmenu_select is [selected]: only the highlight moved.</summary>
    private void HandlePopupmenuSelect(object[] evt)
    {
        if (evt.Length == 0)
        {
            return;
        }

        CompletionSelected = NvimStateHub.ToInt(evt[0]);
        CompletionsChanged?.Invoke();
    }

    private void HandlePopupmenuHide()
    {
        if (CompletionWords.Count == 0 && CompletionSelected < 0)
        {
            return;
        }

        CompletionWords = Array.Empty<string>();
        CompletionSelected = -1;
        CompletionsChanged?.Invoke();
    }

    /// <summary>
    /// msg_show is [kind, content, replace_last, history, append, id, trigger]
    /// (nvim 0.12; older versions stop after replace_last).
    ///
    /// The kind distinguishes ordinary echo from errors, warnings, search counts
    /// and confirmations. Content is an array of [attr_id, text] chunks, like
    /// cmdline_show. Keeping the kind lets the margin colour an error
    /// differently. append continues the message before it, so the pair is
    /// measured together: a list built from several appended pieces is still
    /// one long output. Anything past <see cref="InlineMessageLimit"/> goes
    /// to the pager and leaves the margin empty.
    /// </summary>
    private void HandleMsgShow(object[] evt)
    {
        if (evt.Length == 0)
        {
            return;
        }

        var kind = NvimStateHub.AsString(evt[0]);
        var text = ChunksToText(evt.Length > 1 ? evt[1] : null);

        bool append = evt.Length > 4 && evt[4] is bool a && a;
        if (append)
        {
            string? pager = PagerTextSnapshot();
            if (pager != null)
            {
                text = pager + text;
            }
            else if (Message != null)
            {
                text = Message + text;
            }
        }

        if (CountLines(text) > InlineMessageLimit)
        {
            SetMessage(null!, null!);
            OpenPager(text);
            return;
        }

        SetMessage(kind, text);
    }

    /// <summary>
    /// msg_history_show is [entries, prev_cmd], each entry [kind, content,
    /// append]: the whole :messages history. With ext_messages nvim sends it
    /// here instead of printing it, so :messages used to show nothing at all.
    /// It always goes to the pager, one line per entry.
    /// </summary>
    private void HandleMsgHistoryShow(object[] evt)
    {
        if (evt.Length == 0 || !(evt[0] is object[] entries))
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            if (!(e is object[] entry) || entry.Length < 2)
            {
                continue;
            }

            bool append = entry.Length > 2 && entry[2] is bool a && a;
            if (sb.Length > 0 && !append)
            {
                sb.Append('\n');
            }

            sb.Append(ChunksToText(entry[1]));
        }

        if (sb.Length == 0)
        {
            return;
        }

        OpenPager(sb.ToString());
    }

    private static string ChunksToText(object? content)
    {
        var sb = new StringBuilder();
        if (content is object[] chunks)
        {
            foreach (var c in chunks)
            {
                if (c is object[] chunk && chunk.Length > 1)
                {
                    sb.Append(NvimStateHub.AsString(chunk[1]));
                }
            }
        }

        return sb.ToString();
    }

    private void SetMessage(string kind, string value)
    {
        MessageKind = kind;
        Message = value;
        MessageChanged?.Invoke(value);
    }

    /// <summary>
    /// msg_showmode is [content]: "-- INSERT --", "-- VISUAL --", etc.
    /// </summary>
    private void HandleMsgShowMode(object[] evt)
    {
        var text = ChunksToText(evt.Length > 0 ? evt[0] : null);
        ModeMessage = text;
        ModeMessageChanged?.Invoke(text);
    }

    /// <summary>
    /// msg_showcmd is [content]: the partial command being composed, drawn
    /// bottom-right in Vim. An empty content array means "clear it" - the
    /// command completed or was aborted.
    /// </summary>
    private void HandleMsgShowCmd(object[] evt)
    {
        var text = ChunksToText(evt.Length > 0 ? evt[0] : null);
        ShowCmd = text.Length == 0 ? null! : text;
        ShowCmdChanged?.Invoke(ShowCmd);
    }

    /// <summary>
    /// msg_clear clears everything in the message area, including the mode
    /// indicator and any pending command fragments.
    /// </summary>
    private void ClearMessages()
    {
        // null is the "nothing to show" state Message and ModeMessage document.
        SetMessage(null!, null!);
        ModeMessage = null!;
        ModeMessageChanged?.Invoke(null!);
        ShowCmd = null!;
        ShowCmdChanged?.Invoke(null!);
    }

    private void OpenPager(string text)
    {
        lock (_pagerGate)
        {
            PagerText = text;
        }

        PagerChanged?.Invoke(text);
    }

    private string? PagerTextSnapshot()
    {
        lock (_pagerGate)
        {
            return PagerText;
        }
    }

    /// <summary>Lines in a message, for the pager threshold. Public for tests.</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int lines = 1;
        foreach (char c in text!)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}
