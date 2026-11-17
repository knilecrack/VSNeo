using VSNeo_Extension.Nvim;

namespace VSNeo_Extension.Editor;

/// <summary>
/// The two pending-key state machines behind <c>u</c>/<c>&lt;C-r&gt;</c> inside
/// an i_CTRL-O excursion and after an &lt;Esc&gt; from replace mode.
///
/// Value in, value out: the caller owns the fields and the clock
/// (<see cref="System.Environment.TickCount"/>), so the rules - sequence
/// windows, the two-second guard, unchecked tick wrap - are testable without
/// Visual Studio or a session.
/// </summary>
internal static class CtrlODecision
{
    /// <summary>What the caller should use and store back.</summary>
    public readonly struct Result
    {
        public Result(VimMode mode, bool pending, bool resolved)
        {
            Mode = mode;
            Pending = pending;
            Resolved = resolved;
        }

        public VimMode Mode { get; }

        /// <summary>The pending flag to store back.</summary>
        public bool Pending { get; }

        /// <summary>
        /// Replace-escape only: true when the key resolved as Normal and the
        /// caller must not continue to the Ctrl-O check. Always false for the
        /// Ctrl-O result.
        /// </summary>
        public bool Resolved { get; }
    }

    /// <summary>
    /// An &lt;Esc&gt; sent from replace mode arms a short window in which the
    /// cache's Replace resolves as Normal, so u and Ctrl+R stay Visual
    /// Studio-side until nvim's own mode push lands. A matching Replace mode
    /// with the same mode-sequence number inside 2 s resolves as Normal and
    /// leaves the flag armed; anything else clears it and lets the caller go
    /// on to the Ctrl-O check.
    /// </summary>
    public static Result ResolveReplaceEscape(
        VimMode mode, int modeSequence, int nowTicks,
        bool pending, int armedSequence, int armedTicks)
    {
        if (!pending)
        {
            return new Result(mode, false, false);
        }

        if (mode == VimMode.Replace && modeSequence == armedSequence
            && unchecked(nowTicks - armedTicks) <= 2000)
        {
            return new Result(VimMode.Normal, true, true);
        }

        return new Result(mode, false, false);
    }

    /// <summary>
    /// i_CTRL-O: nvim is in Insert (or Replace) waiting for one normal command.
    /// While the arm is current the cache resolves as Normal, so u / Ctrl+R are
    /// intercepted before nvim sees them (and ReleaseCtrlO sends the &lt;Esc&gt;
    /// that cancels the pending command). The excursion is over once the mode
    /// sequence has advanced by two (i -&gt; n -&gt; i) or the arm went stale.
    /// </summary>
    public static Result ResolveCtrlO(
        VimMode mode, int modeSequence, int nowTicks,
        bool pending, int armedSequence, int armedTicks)
    {
        if (!pending)
        {
            return new Result(mode, false, false);
        }

        if (mode is not (VimMode.Insert or VimMode.Replace))
        {
            return new Result(mode, true, false);
        }

        if (modeSequence >= armedSequence + 2
            || (modeSequence == armedSequence
                && unchecked(nowTicks - armedTicks) > 2000))
        {
            return new Result(mode, false, false);
        }

        return new Result(VimMode.Normal, true, false);
    }
}
