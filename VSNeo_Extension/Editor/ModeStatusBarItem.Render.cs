using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// The badge: the mode name, its colours, and the recording dot; then showcmd and the message.
internal static partial class ModeStatusBarItem
{
    private static void Render()
    {
        if (s_text == null)
        {
            return;
        }

        var state = VSNeo_ExtensionPackage.Session?.State;
        RenderMessage(state != null && Infrastructure.VSNeoSettings.Enabled ? state : null);
        if (state == null)
        {
            s_text.Text = string.Empty;
            s_recBadge.Visibility = Visibility.Collapsed;
            return;
        }

        if (!Infrastructure.VSNeoSettings.Enabled)
        {
            s_text.Text = "VSNeo OFF";
            s_badge.Background = FrozenBrush(0x626262);
            s_text.Foreground = FrozenBrush(0xF5F5F5);
            s_recBadge.Visibility = Visibility.Collapsed;
            return;
        }

        s_text.Text = ModeText(state.Mode, state.VisualKind);
        s_badge.Background = BadgeBrush(state.Mode);
        s_text.Foreground = ContrastBrush(state.Mode);

        var reg = state.RecordingReg;
        if (reg == null)
        {
            s_recBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            s_recText.Text = "REC @" + reg;
            s_recBadge.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The mode text nvim sends ("-- INSERT --") is not shown: the badge
    /// already says it, from the mode the key path routes on. A message of
    /// a few lines (up to the pager threshold) is joined onto the one row;
    /// the tooltip keeps the lines.
    /// </summary>
    private static void RenderMessage(NvimStateHub? state)
    {
        // A view in a floating window draws its message over itself
        // (MessagePager.Floating.cs): this bar may be on another monitor.
        // showcmd stays here - it is glanced at, not read.
        string? message = MessagePager.FocusedViewFloats ? null : state?.Message;
        s_showCmdText.Text = state?.ShowCmd ?? string.Empty;
        s_messageText.Text = OneLine(message);
        s_messageText.ToolTip = string.IsNullOrEmpty(message) ? null : message;
        if (IsError(state?.MessageKind) && !string.IsNullOrEmpty(message))
        {
            s_messageBadge.Background = s_recBadge.Background;
            s_messageText.Foreground = s_recText.Foreground;
        }
        else
        {
            s_messageBadge.Background = null;
            StatusBarText(s_messageText);
        }
    }

    /// <summary>A message of a few lines joined onto one row; the tooltip keeps the lines.</summary>
    internal static string OneLine(string? message) =>
        string.IsNullOrEmpty(message) ? string.Empty : message!.TrimEnd('\n').Replace("\n", "  │  ");

    /// <summary>
    /// The msg_show kinds that are errors (:h ui-messages). Not just emsg:
    /// :echoerr, a failing :lua, a failed RPC call, a shell command's error
    /// output, and vim.notify at ERROR level (sent as echoerr) are errors
    /// too, and in plain text they were easy to miss on the blue and dark
    /// status bars.
    /// </summary>
    internal static bool IsError(string? kind)
    {
        switch (kind)
        {
            case "error":
            case "emsg":
            case "echoerr":
            case "lua_error":
            case "rpc_error":
            case "shell_err":
                return true;
            default:
                return false;
        }
    }

    private static string ModeText(VimMode mode, char visualKind)
    {
        switch (mode)
        {
            case VimMode.Normal:
                return "NORMAL";
            case VimMode.Insert:
                return "INSERT";
            case VimMode.Replace:
                return "REPLACE";
            case VimMode.Visual:
                // The hub's collapsed Visual carries the flavour separately;
                // Vim draws all three and there is no reason to know less here.
                if (visualKind == 'V')
                {
                    return "VISUAL LINE";
                }

                if (visualKind == '\x16')
                {
                    return "VISUAL BLOCK";
                }

                return "VISUAL";
            case VimMode.CmdLine:
                return "COMMAND";
            case VimMode.OperatorPending:
                return "OP-PENDING";
            case VimMode.Terminal:
                return "TERMINAL";
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Badge colors in the Vim statusline tradition: green for normal, blue
    /// for insert, purple for visual, red for replace, amber for the command
    /// line. The palette is deliberately not taken from nvim highlight groups:
    /// 'ModeMsg' is one group for every mode, so it cannot tell them apart,
    /// and per-mode colors are what the eye navigates by.
    /// </summary>
    private static SolidColorBrush BadgeBrush(VimMode mode)
    {
        uint rgb;
        switch (mode)
        {
            case VimMode.Normal:
                rgb = 0x5B8C3E;
                break;
            case VimMode.Insert:
                rgb = 0x2F6FBF;
                break;
            case VimMode.Visual:
                rgb = 0x8A4FA3;
                break;
            case VimMode.Replace:
                rgb = 0xB0392E;
                break;
            case VimMode.CmdLine:
                rgb = 0xB07D1E;
                break;
            case VimMode.OperatorPending:
                rgb = 0x556070;
                break;
            case VimMode.Terminal:
                rgb = 0x2E8B8B;
                break;
            default:
                return Brushes.Transparent;
        }

        return FrozenBrush(rgb);
    }

    /// <summary>White on the dark badges, near-black on the bright ones.</summary>
    private static SolidColorBrush ContrastBrush(VimMode mode)
    {
        switch (mode)
        {
            case VimMode.Normal:
            case VimMode.CmdLine:
                return FrozenBrush(0x1E1E1E);
            default:
                return FrozenBrush(0xF5F5F5);
        }
    }

    private static SolidColorBrush FrozenBrush(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
