using VSNeo_Extension.Editor;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The Paste command is two gestures: the Ctrl+V chord and a plain paste
/// request. These tests pin which one each combination of chord state and mode
/// stands for, and which modes leave the paste to Visual Studio or to the
/// replace/cmdline paths.
/// </summary>
public class PasteDecisionTests
{
    [Theory]
    [InlineData(VimMode.Normal)]
    [InlineData(VimMode.Visual)]
    [InlineData(VimMode.OperatorPending)]
    public void Chord_is_blockwise_wherever_vim_pastes(VimMode mode)
    {
        Assert.Equal(PasteDecision.BlockwiseChord, PasteDecision.RoutedKey(true, mode));
    }

    [Theory]
    [InlineData(VimMode.Normal)]
    [InlineData(VimMode.Visual)]
    [InlineData(VimMode.OperatorPending)]
    public void A_command_paste_stands_for_p_wherever_vim_pastes(VimMode mode)
    {
        Assert.Equal(PasteDecision.CommandKey, PasteDecision.RoutedKey(false, mode));
        Assert.True(PasteDecision.IsVimPaste(mode));
    }

    [Theory]
    [InlineData(VimMode.Insert)]
    [InlineData(VimMode.Replace)]
    [InlineData(VimMode.CmdLine)]
    [InlineData(VimMode.Unknown)]
    public void Other_modes_are_not_ours(VimMode mode)
    {
        Assert.False(PasteDecision.IsVimPaste(mode));
        Assert.Null(PasteDecision.RoutedKey(false, mode));
    }

    [Theory]
    [InlineData(VimMode.Insert)]
    [InlineData(VimMode.Replace)]
    [InlineData(VimMode.CmdLine)]
    public void The_chord_is_named_in_every_mode_so_sethandler_can_claim_it(VimMode mode)
    {
        // Insert keeps Visual Studio's paste unless the user claims the chord
        // with :Sethandler <C-v> i:vim (nvim's literal-next), so the key is
        // still named there; replace and the command line have their own
        // paste paths.
        Assert.Equal(PasteDecision.BlockwiseChord, PasteDecision.RoutedKey(true, mode));
        Assert.False(PasteDecision.IsVimPaste(mode));
    }

    [Fact]
    public void Chord_and_command_never_route_the_same_key()
    {
        foreach (VimMode mode in new[]
                 {
                     VimMode.Normal, VimMode.Visual, VimMode.OperatorPending,
                     VimMode.Insert, VimMode.Replace, VimMode.CmdLine,
                 })
        {
            string? chord = PasteDecision.RoutedKey(true, mode);
            string? command = PasteDecision.RoutedKey(false, mode);
            Assert.NotEqual(chord, command);
        }
    }
}
