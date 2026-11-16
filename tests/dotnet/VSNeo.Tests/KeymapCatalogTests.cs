using System.Collections.Generic;
using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// The mapping tables the which-key popup and the insert-mapping claim read on
/// the key path. Tokenization and prefix matching are the parts that used to
/// live inside NvimStateHub and could only be exercised through the RPC hub.
/// </summary>
public class KeymapCatalogTests
{
    private static KeymapEntry Entry(string lhs, string desc = "") => new(lhs, desc);

    [Fact]
    public void Split_folds_named_keys_and_lowercases_them()
    {
        Assert.Equal(new[] { "<c-w>", "f" }, KeymapCatalog.SplitKeyTokens("<C-w>f"));
        Assert.Equal(new[] { "<c-w>" }, KeymapCatalog.SplitKeyTokens("<C-W>"));
        Assert.Equal(new[] { " " }, KeymapCatalog.SplitKeyTokens("<Space>"));
        Assert.Equal(new[] { " " }, KeymapCatalog.SplitKeyTokens("<space>"));
        Assert.Equal(new[] { "<left>" }, KeymapCatalog.SplitKeyTokens("<Left>"));
        Assert.Equal(new[] { "g", "g" }, KeymapCatalog.SplitKeyTokens("gg"));
        Assert.Equal(new[] { "<" }, KeymapCatalog.SplitKeyTokens("<"));
        Assert.Empty(KeymapCatalog.SplitKeyTokens(string.Empty));
    }

    [Fact]
    public void Replace_swaps_the_whole_table_for_a_mode()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[] { Entry("gg"), Entry("<C-w>f") });

        IReadOnlyList<KeymapEntry> entries = catalog.KeymapsFor(VimMode.Normal);
        Assert.Equal(2, entries.Count);
        Assert.Equal("gg", entries[0].Lhs);
        Assert.Equal("<C-w>f", entries[1].Lhs);
    }

    [Fact]
    public void Children_match_a_prefix_through_normalized_tokens()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[]
        {
            Entry("<C-w>f"),
            Entry("<C-w>v"),
            Entry("<C-w><C-w>"),
            Entry("gg"),
        });

        IReadOnlyList<KeymapEntry> children = catalog.KeymapChildren(VimMode.Normal, "<C-W>");
        Assert.Equal(3, children.Count);
        Assert.All(children, e => Assert.StartsWith("<C-w>", e.Lhs, System.StringComparison.Ordinal));
        Assert.True(catalog.HasKeymapChildren(VimMode.Normal, "<c-w>"));
    }

    [Fact]
    public void Space_prefix_matches_both_spellings()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[] { Entry("<Space>x"), Entry("<Space>y") });

        Assert.Equal(2, catalog.KeymapChildren(VimMode.Normal, "<Space>").Count);
        Assert.Equal(2, catalog.KeymapChildren(VimMode.Normal, " ").Count);
    }

    [Fact]
    public void A_complete_mapping_is_not_a_child()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[] { Entry("<C-w>f") });

        Assert.Empty(catalog.KeymapChildren(VimMode.Normal, "<C-w>f"));
        Assert.False(catalog.HasKeymapChildren(VimMode.Normal, "<C-w>f"));
    }

    [Fact]
    public void Unknown_or_empty_prefixes_show_nothing()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[] { Entry("gg") });

        Assert.Empty(catalog.KeymapChildren(VimMode.Normal, "z"));
        Assert.Empty(catalog.KeymapChildren(VimMode.Normal, string.Empty));
        Assert.False(catalog.HasKeymapChildren(VimMode.Normal, "z"));
        Assert.False(catalog.HasKeymapChildren(VimMode.Normal, string.Empty));
    }

    [Fact]
    public void Visual_and_normal_tables_are_separate()
    {
        var catalog = new KeymapCatalog();
        catalog.Replace(VimMode.Normal, new[] { Entry("gg") });
        catalog.Replace(VimMode.Visual, new[] { Entry("y") });

        Assert.Equal("gg", Assert.Single(catalog.KeymapsFor(VimMode.Normal)).Lhs);
        Assert.Equal("y", Assert.Single(catalog.KeymapsFor(VimMode.Visual)).Lhs);
        Assert.Empty(catalog.KeymapsFor(VimMode.OperatorPending));
        Assert.Empty(catalog.KeymapsFor(VimMode.Insert));
    }

    [Fact]
    public void Insert_mappings_are_case_insensitive_and_replaced_whole()
    {
        var catalog = new KeymapCatalog();
        catalog.SetInsert(new[] { "<Left>", string.Empty });

        Assert.True(catalog.IsInsertMapped("<Left>"));
        Assert.True(catalog.IsInsertMapped("<left>"));
        Assert.False(catalog.IsInsertMapped("<Right>"));

        catalog.SetInsert(new[] { "<Down>" });
        Assert.False(catalog.IsInsertMapped("<Left>"));
        Assert.True(catalog.IsInsertMapped("<down>"));
    }
}
