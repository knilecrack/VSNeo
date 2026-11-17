using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading; // TaskScheduler.GetAwaiter, for "await TaskScheduler.Default"
using VSNeo_Extension.Infrastructure;

namespace VSNeo_Extension.Nvim;

// The notification switchboard: every nvim message routed to the event it feeds.
internal sealed partial class NvimSession
{
    /// <summary>The buffer a nvim_buf_*_event names in its first argument, or -1.</summary>
    private static long BufferIdOf(object[] args)
    {
        if (args == null || args.Length == 0)
        {
            return -1;
        }

        if (args[0] is NvimHandle h)
        {
            return h.Id;
        }

        try
        { return args[0] == null ? -1 : Convert.ToInt64(args[0]); }
        catch { return -1; }
    }

    private void OnNotification(string method, object[] args)
    {
        switch (method)
        {
            // Per-subscriber isolation: one mirror per open document listens
            // here, and a plain multicast Invoke stops at the first throw -
            // every mirror subscribed after it would miss the event and drift
            // silently until Verify caught it.
            case "nvim_buf_lines_event":
                Fanout.Invoke(BufferLinesChanged, args, "BufferLinesChanged");
                Fanout.Invoke(RemoteBufferChanged, BufferIdOf(args), "RemoteBufferChanged");
                break;
            case "nvim_buf_changedtick_event":
                Fanout.Invoke(RemoteBufferChanged, BufferIdOf(args), "RemoteBufferChanged");
                break;
            case "nvim_buf_detach_event":
                Fanout.Invoke(BufferDetached, args, "BufferDetached");
                break;
            case "vsneo_action":
                if (args != null && args.Length > 0)
                {
                    ActionRequested?.Invoke(
                        NvimStateHub.AsString(args[0]),
                        args.Length > 1 ? NvimStateHub.AsString(args[1]) : string.Empty);
                }

                break;
            case "vsneo_toggle":
                ToggleRequested?.Invoke(args != null && args.Length > 0
                    ? NvimStateHub.AsString(args[0]) : string.Empty);
                break;
            case "vsneo_sethandler":
            {
                string definition = args != null && args.Length > 0
                    ? NvimStateHub.AsString(args[0]) : string.Empty;
                if (!State.Routing.Apply(definition, out string error))
                {
                    _ = RequestAsync("nvim_exec_lua",
                        "local text = ...; vim.api.nvim_echo({ { text, 'ErrorMsg' } }, true, {})",
                        new object[] { "VSNeo: sethandler: " + error }).ContinueWith(
                        t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
            }

            break;
            case "vsneo_select_action":
                // A command that changes the selection (Edit.ExpandSelection):
                // run it, then hand the selection to nvim as visual mode.
                if (args != null && args.Length > 0)
                {
                    SelectActionRequested?.Invoke(NvimStateHub.AsString(args[0]));
                }

                break;
            case "vsneo_range_action":
                // [command, kind, startRow, startByte, endRow, endByte]: an
                // operator's range (vsneo.operator), 1-based rows, 0-based
                // byte columns, end inclusive.
                if (args != null && args.Length >= 6)
                {
                    try
                    {
                        RangeActionRequested?.Invoke(new RangeAction(
                            NvimStateHub.AsString(args[0]), NvimStateHub.AsString(args[1]),
                            Convert.ToInt32(args[2]), Convert.ToInt32(args[3]),
                            Convert.ToInt32(args[4]), Convert.ToInt32(args[5])));
                    }
                    catch (Exception ex)
                    {
                        Log.Write("malformed vsneo_range_action", ex);
                    }
                }

                break;
            case "vsneo_focus":
                if (args != null && args.Length > 0)
                {
                    FocusRequested?.Invoke(NvimStateHub.AsString(args[0]));
                }

                break;
            case "vsneo_mru":
                MruRequested?.Invoke();
                break;
            case "vsneo_tabs":
                TabJumpRequested?.Invoke();
                break;
            case "vsneo_tab_pick":
                if (args != null && args.Length > 0)
                {
                    TabJumpPicked?.Invoke(NvimStateHub.AsString(args[0]));
                }

                break;
            case "vsneo_seeky_config":
                // [palette map|nil, prompt_normal 0/1]: see send_seeky_config in vsneo.lua.
                if (args != null && args.Length > 1)
                {
                    Dictionary<string, string>? palette = null;
                    if (args[0] is IDictionary<string, object?> map)
                    {
                        palette = new Dictionary<string, string>();
                        foreach (var entry in map)
                        {
                            if (entry.Value != null)
                            {
                                palette[entry.Key] = NvimStateHub.AsString(entry.Value);
                            }
                        }
                    }

                    VSNeo.Seeky.SeekyPickerController.Configure(palette, NvimStateHub.AsString(args[1]) == "1");
                }

                break;
            case "vsneo_seeky":
                if (args != null && args.Length > 0)
                {
                    SeekyRequested?.Invoke(
                        NvimStateHub.AsString(args[0]),
                        args.Length > 1 ? NvimStateHub.AsString(args[1]) : string.Empty);
                }

                break;
        }
    }
}
