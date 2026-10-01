using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// How many history steps one u takes when typing goes through nvim: every
/// transaction added in an insert session joins that session's group.
/// </summary>
public class UndoGroupStackTests
{
    [Fact]
    public void An_insert_session_undoes_as_one_step()
    {
        var s = new UndoGroupStack();
        s.Added(inInsert: false);            // dd
        for (int i = 0; i < 5; i++) s.Added(inInsert: true);   // typed "hello"
        s.InsertEnded();

        Assert.Equal(5, s.TakeUndo());
        Assert.Equal(1, s.TakeUndo());
        Assert.Equal(1, s.TakeUndo());       // past what was tracked: one step
    }

    [Fact]
    public void Two_inserts_are_two_groups_even_back_to_back()
    {
        var s = new UndoGroupStack();
        s.Added(true); s.Added(true);
        s.InsertEnded();
        s.Added(true); s.Added(true); s.Added(true);
        s.InsertEnded();

        Assert.Equal(3, s.TakeUndo());
        Assert.Equal(2, s.TakeUndo());
    }

    [Fact]
    public void A_change_commands_deletion_joins_its_insert()
    {
        // cw: the deletion lands with the mode already reading insert.
        var s = new UndoGroupStack();
        s.Added(true);                       // the deleted word
        s.Added(true); s.Added(true);        // typed text
        s.InsertEnded();
        Assert.Equal(3, s.TakeUndo());
    }

    [Fact]
    public void Redo_replays_the_group_and_a_new_edit_clears_redo()
    {
        var s = new UndoGroupStack();
        s.Added(true); s.Added(true); s.Added(true);
        s.InsertEnded();

        Assert.Equal(3, s.TakeUndo());
        Assert.Equal(3, s.TakeRedo());
        Assert.Equal(3, s.TakeUndo());

        s.Added(false);
        Assert.Equal(1, s.TakeRedo());       // nothing left to redo as a group
        Assert.Equal(1, s.TakeUndo());
    }

    [Fact]
    public void An_undo_closes_the_live_group()
    {
        // u via <C-o>u mid-insert: typing after it starts a new group.
        var s = new UndoGroupStack();
        s.Added(true); s.Added(true);
        Assert.Equal(2, s.TakeUndo());
        s.Added(true);
        Assert.Equal(1, s.TakeUndo());
    }

    [Fact]
    public void Reset_falls_back_to_single_steps()
    {
        var s = new UndoGroupStack();
        s.Added(true); s.Added(true);
        s.Reset();
        Assert.Equal(1, s.TakeUndo());
        Assert.Equal(1, s.TakeRedo());
    }
}
