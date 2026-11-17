using VSNeo_Extension.Editor;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The insert-mode and typed-text routing conditions. The key processor keeps
/// the side effects and their order; these tests pin the conditions that
/// decide whether a key or character is ours.
/// </summary>
public class InsertKeyDecisionTests
{
    [Theory]
    [InlineData(true, true, true, false, false, true, false, true)]  // normal insert
    [InlineData(true, true, true, false, false, true, true, false)]  // IntelliSense gates insert
    [InlineData(true, true, true, false, false, false, true, true)]  // normal mode ignores the gate
    [InlineData(true, true, true, false, true, true, true, true)]    // i:vim overrides the gate
    [InlineData(true, true, true, false, false, false, false, true)] // replace ignores the gate
    [InlineData(false, true, true, false, false, false, false, false)] // not a document view
    [InlineData(true, false, true, false, false, false, false, false)] // session not ready
    [InlineData(true, true, false, false, false, false, false, false)] // no focus
    [InlineData(true, true, true, true, false, false, false, false)]   // foreign focus
    public void Intercept_conditions(
        bool documentView, bool ready, bool focus, bool foreign, bool forceVim,
        bool insertMode, bool intelliSense, bool expected)
    {
        Assert.Equal(expected, InsertKeyDecision.ShouldIntercept(
            documentView, ready, focus, foreign, forceVim, insertMode, intelliSense));
    }

    [Fact]
    public void Ctrl_w_is_delete_word_in_insert_only()
    {
        Assert.True(InsertKeyDecision.ShouldDeleteWordBackward(
            true, true, true, false, false, false, VimMode.Insert, true));
        Assert.False(InsertKeyDecision.ShouldDeleteWordBackward(
            true, true, true, false, true, false, VimMode.Insert, true));  // Ctrl-O pending
        Assert.False(InsertKeyDecision.ShouldDeleteWordBackward(
            true, true, true, false, false, true, VimMode.Insert, true));   // i:vim owns it
        Assert.False(InsertKeyDecision.ShouldDeleteWordBackward(
            true, true, true, false, false, false, VimMode.Normal, true));
        Assert.False(InsertKeyDecision.ShouldDeleteWordBackward(
            true, true, true, false, false, false, VimMode.Insert, false));
    }

    [Fact]
    public void Completion_is_insert_side_unless_an_imap_owns_the_key()
    {
        Assert.True(InsertKeyDecision.ShouldRouteCompletion(
            true, true, true, false, false, false, VimMode.Insert, true, false, false));
        Assert.True(InsertKeyDecision.ShouldRouteCompletion(
            true, true, true, false, false, false, VimMode.Insert, true, true, true));
        // A user's imap wins while no popup is open.
        Assert.False(InsertKeyDecision.ShouldRouteCompletion(
            true, true, true, false, false, false, VimMode.Insert, true, true, false));
        Assert.False(InsertKeyDecision.ShouldRouteCompletion(
            true, true, true, false, false, true, VimMode.Insert, true, false, false));
        Assert.False(InsertKeyDecision.ShouldRouteCompletion(
            true, true, true, false, false, false, VimMode.Normal, true, false, false));
    }

    [Fact]
    public void Escape_and_replace_keys_are_claimed_only_there()
    {
        Assert.True(InsertKeyDecision.ShouldSendEscape(VimMode.Insert, true));
        Assert.True(InsertKeyDecision.ShouldSendEscape(VimMode.Replace, true));
        Assert.False(InsertKeyDecision.ShouldSendEscape(VimMode.Normal, true));

        Assert.True(InsertKeyDecision.ShouldSendReplaceKey(VimMode.Replace, true));
        Assert.False(InsertKeyDecision.ShouldSendReplaceKey(VimMode.Replace, false));
        Assert.False(InsertKeyDecision.ShouldSendReplaceKey(VimMode.Insert, true));
    }

    [Fact]
    public void Ctrl_o_is_insert_s_only()
    {
        Assert.True(InsertKeyDecision.ShouldClaimCtrlO(VimMode.Insert, true));
        // In replace mode the replace branch sends <C-o> and arms it itself.
        Assert.False(InsertKeyDecision.ShouldClaimCtrlO(VimMode.Replace, true));
        Assert.False(InsertKeyDecision.ShouldClaimCtrlO(VimMode.Normal, true));
    }

    [Fact]
    public void Insert_maps_need_a_key_and_an_owner()
    {
        Assert.True(InsertKeyDecision.ShouldClaimInsertMap(VimMode.Insert, true, false, true));
        Assert.True(InsertKeyDecision.ShouldClaimInsertMap(VimMode.Insert, true, true, false));
        Assert.False(InsertKeyDecision.ShouldClaimInsertMap(VimMode.Insert, false, true, true));
        Assert.False(InsertKeyDecision.ShouldClaimInsertMap(VimMode.Replace, true, true, true));
    }

    [Fact]
    public void Redo_is_normal_mode_only()
    {
        Assert.True(InsertKeyDecision.ShouldRedo(VimMode.Normal, true));
        Assert.False(InsertKeyDecision.ShouldRedo(VimMode.Insert, true));
        Assert.False(InsertKeyDecision.ShouldRedo(VimMode.Normal, false));
    }

    [Fact]
    public void Typed_text_is_routed_in_replace_or_behind_a_remote_edit()
    {
        Assert.True(InsertKeyDecision.ShouldRouteTypedText(VimMode.Replace, true, false));
        Assert.True(InsertKeyDecision.ShouldRouteTypedText(VimMode.Insert, false, true));
        Assert.False(InsertKeyDecision.ShouldRouteTypedText(VimMode.Insert, false, false));
        Assert.False(InsertKeyDecision.ShouldRouteTypedText(VimMode.Normal, true, true));
    }

    [Fact]
    public void Undo_is_normal_mode_u()
    {
        Assert.True(InsertKeyDecision.ShouldUndoText(VimMode.Normal, "u"));
        Assert.False(InsertKeyDecision.ShouldUndoText(VimMode.Insert, "u"));
        Assert.False(InsertKeyDecision.ShouldUndoText(VimMode.Normal, "x"));
    }

    [Fact]
    public void Ctrl_alt_control_output_stays_with_vs()
    {
        Assert.True(InsertKeyDecision.ShouldLeaveCtrlAltText(true, true));
        Assert.False(InsertKeyDecision.ShouldLeaveCtrlAltText(true, false));
        Assert.False(InsertKeyDecision.ShouldLeaveCtrlAltText(false, true));
    }

    [Theory]
    [InlineData(VimMode.Normal, "\"", true)]
    [InlineData(VimMode.Visual, "\"", true)]
    [InlineData(VimMode.Insert, "\"", false)]
    [InlineData(VimMode.Normal, "x", false)]
    public void Register_peek_is_normal_and_visual(VimMode mode, string text, bool expected)
    {
        Assert.Equal(expected, InsertKeyDecision.ShouldShowRegisterPeek(mode, text));
    }

    [Theory]
    [InlineData(VimMode.Normal, "`", true)]
    [InlineData(VimMode.Normal, "'", true)]
    [InlineData(VimMode.Visual, "`", true)]
    [InlineData(VimMode.Insert, "`", false)]
    [InlineData(VimMode.Normal, "x", false)]
    public void Mark_peek_is_normal_and_visual(VimMode mode, string text, bool expected)
    {
        Assert.Equal(expected, InsertKeyDecision.ShouldShowMarkPeek(mode, text));
    }
}
