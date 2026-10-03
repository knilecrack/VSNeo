using VSNeo_Extension.Editor;
using Xunit;

namespace VSNeo.Tests;

// States are ReiteratedVersionNumbers: fresh edits count up, an undo lands
// on the number of the version whose text comes back. The walk itself runs
// against Visual Studio's history and is checked live; these rows pin the
// decision of when to walk and where to.
public class InsertUndoGroupTests
{
    [Fact]
    public void ChangeThroughInsertUndoesAndRedoesAsOneStep()
    {
        var g = new InsertUndoGroup();
        g.Begin(10);            // c3w: deletion lands at 11, typing at 12..14
        g.End(14);              // <Esc>

        Assert.Equal(10, g.Target(14, undo: true));    // u walks 14 -> 10
        Assert.Null(g.Target(10, undo: true));         // a second u is an ordinary step
        Assert.Equal(14, g.Target(10, undo: false));   // <C-r> walks 10 -> 14
        Assert.Null(g.Target(14, undo: false));
    }

    [Fact]
    public void ElsewhereInHistoryIsASingleStep()
    {
        var g = new InsertUndoGroup();
        g.Begin(10);
        g.End(14);

        Assert.Null(g.Target(12, undo: true));   // Ctrl+Z inside the range
        Assert.Null(g.Target(20, undo: true));   // a later change owns undo now
        Assert.Null(g.Target(20, undo: false));
    }

    [Fact]
    public void EmptySessionKeepsThePreviousGroup()
    {
        var g = new InsertUndoGroup();
        g.Begin(10);
        g.End(14);
        g.Begin(14);            // i<Esc>
        g.End(14);

        Assert.Equal(10, g.Target(14, undo: true));
    }

    [Fact]
    public void BeginIsIdempotentWhileOpen()
    {
        var g = new InsertUndoGroup();
        g.Begin(10);            // the drain, before the deletion
        g.Begin(11);            // the mode push, after it
        g.End(13);

        Assert.Equal(10, g.Target(13, undo: true));
        Assert.False(g.IsOpen);
    }

    [Fact]
    public void NothingRecordedMeansSingleSteps()
    {
        var g = new InsertUndoGroup();
        Assert.Null(g.Target(5, undo: true));
        g.End(7);               // End without Begin is a no-op
        Assert.Null(g.Target(7, undo: true));
    }
}
