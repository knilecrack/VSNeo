using System.Collections.Generic;
using VSNeo.Seeky;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The picker's symbol list is one row per class, not per file: a partial
/// class is reported once per part by the engine's index, and a search for
/// the type should not enumerate its parts. Ordinary same-named classes are
/// untouched.
/// </summary>
public class SeekySymbolCollapseTests
{
    private static SeekyEngineClient.SymbolHit Hit(string path, string text, string kind, string name) =>
        new(path, 1, 0, text, kind, name, [], null, false);

    [Fact]
    public void A_partial_class_is_one_row()
    {
        var hits = new List<SeekyEngineClient.SymbolHit>
        {
            Hit("VSNeo.Seeky/SeekyEngineClient.cs", "internal sealed partial class SeekyEngineClient : IDisposable", "class", "SeekyEngineClient"),
            Hit("VSNeo.Seeky/SeekyEngineClient.Transport.cs", "internal sealed partial class SeekyEngineClient", "class", "SeekyEngineClient"),
        };

        var kept = SeekySymbolCollapse.PartialClasses(hits);

        var single = Assert.Single(kept);
        Assert.Equal("VSNeo.Seeky/SeekyEngineClient.cs", single.Path);
    }

    [Fact]
    public void Unrelated_classes_that_share_a_name_are_both_kept()
    {
        // Program in two projects, neither partial: the index is reporting two
        // real types and collapsing them would hide one.
        var hits = new List<SeekyEngineClient.SymbolHit>
        {
            Hit("src/A/Program.cs", "internal class Program", "class", "Program"),
            Hit("src/B/Program.cs", "internal class Program", "class", "Program"),
        };

        Assert.Equal(2, SeekySymbolCollapse.PartialClasses(hits).Count);
    }

    [Fact]
    public void A_partial_part_does_not_swallow_a_separate_class_with_the_same_name()
    {
        var hits = new List<SeekyEngineClient.SymbolHit>
        {
            Hit("src/A/Settings.cs", "internal sealed partial class Settings", "class", "Settings"),
            Hit("src/B/Settings.cs", "internal sealed class Settings", "class", "Settings"),
        };

        // The plain one is not a part of the partial one, and the partial one
        // is the only partial hit for its name, so both survive.
        Assert.Equal(2, SeekySymbolCollapse.PartialClasses(hits).Count);
    }

    [Fact]
    public void Kind_is_part_of_the_identity()
    {
        // A partial class and a method of the same name are different symbols.
        var hits = new List<SeekyEngineClient.SymbolHit>
        {
            Hit("A.cs", "internal sealed partial class SeekyEngineClient", "class", "SeekyEngineClient"),
            Hit("A.cs", "internal SeekyEngineClient(Func<string> enginePath)", "method", "SeekyEngineClient"),
        };

        Assert.Equal(2, SeekySymbolCollapse.PartialClasses(hits).Count);
    }

    [Fact]
    public void Order_survives_and_the_best_ranked_part_wins()
    {
        var hits = new List<SeekyEngineClient.SymbolHit>
        {
            Hit("first.cs", "internal sealed partial class Zed", "class", "Zed"),
            Hit("other.cs", "internal sealed class Other", "class", "Other"),
            Hit("second.cs", "internal sealed partial class Zed", "class", "Zed"),
        };

        var kept = SeekySymbolCollapse.PartialClasses(hits);

        Assert.Equal(2, kept.Count);
        Assert.Equal("first.cs", kept[0].Path);
        Assert.Equal("other.cs", kept[1].Path);
    }

    [Fact]
    public void An_empty_list_stays_empty() =>
        Assert.Empty(SeekySymbolCollapse.PartialClasses(new List<SeekyEngineClient.SymbolHit>()));
}
