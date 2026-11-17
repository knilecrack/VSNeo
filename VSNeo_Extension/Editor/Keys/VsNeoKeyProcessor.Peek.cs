using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

// which-key tracking and the register/mark peeks.
internal sealed partial class VsNeoKeyProcessor
{
    /// <summary>
    /// Feeds the which-key popup. The key just went to nvim; if the sequence
    /// typed so far is a strict prefix of some mapping, the popup's delay
    /// timer (re)arms, and a completed or dead sequence cancels it. The
    /// lookup is a scan of the hub's cached mapping table - no I/O, which
    /// keeps the key-path invariant intact.
    ///
    /// Counts and operators muddy the picture deliberately little: a count
    /// digit or a d/y/c that leads nowhere simply fails the prefix test and
    /// resets, which is the right answer - the hint is about mappings, not
    /// about Vim grammar.
    /// </summary>
    private void TrackWhichKey(NvimSession session, string keys, VimMode mode)
    {
        // A sent key resolves any pending mark peek (the mark letter, or
        // Escape aborting it); the popup ignores this when none is up.
        var peek = _peekPopup ?? (_view.Properties.TryGetProperty(typeof(PeekPopup), out PeekPopup p)
            ? (_peekPopup = p) : null);
        peek?.OnKeySent();

        if (mode != VimMode.Normal && mode != VimMode.Visual)
        {
            _whichKeyPrefix = string.Empty;
            return;
        }

        var candidate = _whichKeyPrefix + keys;
        bool hasChildren;
        try
        {
            hasChildren = session.State.HasKeymapChildren(mode, candidate);
        }
        catch (Exception ex)
        {
            // A hint lookup must never eat a keystroke's follow-through.
            Infrastructure.Log.Write("which-key lookup failed", ex);
            hasChildren = false;
        }

        if (hasChildren)
        {
            _whichKeyPrefix = candidate;
            var popup = _whichKeyPopup ?? (_view.Properties.TryGetProperty(typeof(WhichKeyPopup), out WhichKeyPopup w)
                ? (_whichKeyPopup = w) : null);
            popup?.Track(candidate, mode);
        }
        else
        {
            ResetWhichKey();
        }
    }

    // Resolved lazily from the view's property bag, then cached: the bag
    // lookup ran per keystroke. Both popups may legitimately not exist yet
    // (created on first use elsewhere), so only a found one is cached.
    private PeekPopup _peekPopup = null!;
    private WhichKeyPopup _whichKeyPopup = null!;

    private void ResetWhichKey()
    {
        _whichKeyPrefix = string.Empty;
        var popup = _whichKeyPopup ?? (_view.Properties.TryGetProperty(typeof(WhichKeyPopup), out WhichKeyPopup w)
            ? (_whichKeyPopup = w) : null);
        popup?.Cancel();
    }

    /// <summary>
    /// Fire-and-forget fetch for a peek (registers or marks). The decision
    /// to fetch is local (cached mode, typed character); the round trip is
    /// async and its result is informational, so the zero-I/O key-path
    /// invariant holds. Dismissal is not tracked here at all: nvim clears
    /// showcmd when the pending prefix resolves, and the popup hears
    /// ShowCmdChanged itself.
    /// </summary>
    private void FetchPeek(NvimSession session, string what)
    {
        var requestedAt = Environment.TickCount;
        var request = session.RequestAsync(
            "nvim_exec_lua",
            "return vsneo." + what + "()",
            Array.Empty<object>());

        var dispatcher = _view.VisualElement.Dispatcher;
        _ = request.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                // A faulted task always carries its exception, so this
                // dereference cannot be null; Log.Write only needs the object.
                Infrastructure.Log.Write(what + " request failed",
                                         t.Exception!.GetBaseException());
                return;
            }

            if (!(t.Result is object[] rows))
            {
                return;
            }

            var parsed = new List<string[]>(rows.Length);
            foreach (var row in rows)
            {
                if (row is object[] pair && pair.Length >= 2)
                {
                    var name = AsString(pair[0]);
                    var preview = AsString(pair[1]);
                    if (name != null && preview != null)
                    {
                        parsed.Add(new[] { name, preview });
                    }
                }
            }

            if (parsed.Count == 0)
            {
                return;
            }

#pragma warning disable VSTHRD001
            // The DispatcherOperation result is deliberately unobserved:
            // the show methods guard staleness themselves and have nothing
            // to report.
            _ = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_view.IsClosed)
                {
                    return;
                }

                if (_view.Properties.TryGetProperty(typeof(PeekPopup), out PeekPopup popup))
                {
                    if (what == "marks")
                    {
                        popup.ShowMarks(parsed, requestedAt);
                    }
                    else
                    {
                        popup.ShowRows(parsed, requestedAt);
                    }
                }
            }));
#pragma warning restore VSTHRD001
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>msgpack strings may arrive as byte[]; the hub's AsString twin.</summary>
    private static string AsString(object value)
    {
        if (value is string s)
        {
            return s;
        }

        if (value is byte[] b)
        {
            return System.Text.Encoding.UTF8.GetString(b);
        }

        return null!;
    }
}
