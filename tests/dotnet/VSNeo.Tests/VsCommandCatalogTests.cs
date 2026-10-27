using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

public class VsCommandCatalogTests
{
    private static readonly VsCommandInfo[] Commands =
    {
        new("Window.CloseAllDocuments", "Close All Documents", "Global::Ctrl+K, Ctrl+W"),
        new("Edit.GoToDefinition", "Go To Definition", "Text Editor::F12"),
        new("Debug.Start", "Start Debugging", "Global::F5"),
    };

    [Fact]
    public void Catalog_is_sorted_by_canonical_name()
    {
        var result = VsCommandCatalog.Sort(Commands);
        Assert.Equal(new[] { "Debug.Start", "Edit.GoToDefinition", "Window.CloseAllDocuments" },
            result.Select(c => c.Name).ToArray());
    }
}
