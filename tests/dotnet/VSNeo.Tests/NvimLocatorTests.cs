using System;
using System.IO;
using System.Linq;
using VSNeo_Extension.Infrastructure;
using Xunit;

namespace VSNeo.Tests;

[CollectionDefinition("Process-wide environment", DisableParallelization = true)]
public class ProcessWideEnvironmentCollection
{
}

/// <summary>
/// The lookup the package runs at load, which is the only prerequisite check
/// a VSIX can have. The override wins and accepts a directory as well as the
/// file; the installer locations are the ones winget, the MSI, scoop and
/// chocolatey use; an override pointing nowhere falls through to the rest.
/// </summary>
[Collection("Process-wide environment")]
public class NvimLocatorTests : IDisposable
{
    private readonly string? _savedOverride = Environment.GetEnvironmentVariable(NvimLocator.PathVariable);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vsneo-locator-" + Guid.NewGuid().ToString("n"));

    public NvimLocatorTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NvimLocator.PathVariable, _savedOverride);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void The_override_wins_as_a_file()
    {
        var exe = Path.Combine(_dir, "nvim.exe");
        File.WriteAllText(exe, "");
        Environment.SetEnvironmentVariable(NvimLocator.PathVariable, exe);

        Assert.Equal(exe, NvimLocator.Find());
    }

    [Fact]
    public void The_override_accepts_the_install_directory()
    {
        var exe = Path.Combine(_dir, "bin", "nvim.exe");
        File.WriteAllText(exe, "");
        Environment.SetEnvironmentVariable(NvimLocator.PathVariable, _dir);

        Assert.Equal(exe, NvimLocator.Find());
    }

    [Fact]
    public void An_override_pointing_nowhere_falls_through()
    {
        Environment.SetEnvironmentVariable(NvimLocator.PathVariable, Path.Combine(_dir, "missing", "nvim.exe"));

        var found = NvimLocator.Find();
        Assert.True(found == null || File.Exists(found));
        Assert.NotEqual(Path.Combine(_dir, "missing", "nvim.exe"), found);
    }

    [Fact]
    public void Installer_locations_cover_winget_the_msi_scoop_and_chocolatey()
    {
        var locations = NvimLocator.WellKnownLocations().ToList();

        Assert.All(locations, l => Assert.EndsWith("nvim.exe", l, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(locations, l => l.Contains(Path.Combine("Neovim", "bin")));
        Assert.Contains(locations, l => l.Contains("scoop"));
        Assert.Contains(locations, l => l.Contains("neovim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_minimum_version_is_what_the_companion_needs()
    {
        // vim.fs and nvim_set_option_value arrived in 0.8; 0.9 is the floor.
        Assert.Equal(new Version(0, 9, 0), NvimLocator.MinimumVersion);
    }
}
