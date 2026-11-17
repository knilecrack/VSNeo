using VSNeo_Extension.Nvim;
using Xunit;

namespace VSNeo.Tests;

/// <summary>
/// Where nvim's config directory lands for each shape of the setting. nvim
/// takes a location only as XDG_CONFIG_HOME + NVIM_APPNAME, so a directory
/// must split into exactly those two.
/// </summary>
public class NvimLaunchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")] // no leaf to name the app: the default, not a crash
    public void Empty_or_unsplittable_is_the_default_app_name(string? setting)
    {
        var env = NvimLaunch.Environment(setting);

        Assert.Equal("vsneo", env["NVIM_APPNAME"]);
        Assert.False(env.ContainsKey("XDG_CONFIG_HOME"));
    }

    [Theory]
    [InlineData(@"C:\Users\A B\AppData\Local\nvim", @"C:\Users\A B\AppData\Local", "nvim")]
    [InlineData(@"D:\dots\vs config\", @"D:\dots", "vs config")]
    [InlineData("\"D:/dots/work\"", @"D:\dots", "work")]
    public void A_directory_splits_into_config_home_and_app_name(string setting, string parent, string leaf)
    {
        var env = NvimLaunch.Environment(setting);

        Assert.Equal(parent, env["XDG_CONFIG_HOME"]);
        Assert.Equal(leaf, env["NVIM_APPNAME"]);
    }

    [Fact]
    public void Environment_variables_and_tilde_expand()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Environment.SetEnvironmentVariable("VSNEO_TEST_ROOT", @"E:\cfg");

        Assert.Equal(@"E:\cfg", NvimLaunch.Environment(@"%VSNEO_TEST_ROOT%\mine")["XDG_CONFIG_HOME"]);
        Assert.Equal(profile, NvimLaunch.Environment("~/vsneo-config")["XDG_CONFIG_HOME"]);
    }

    [Fact]
    public void The_reported_directory_follows_nvims_own_rule()
    {
        string? Local(string name) => name == "LOCALAPPDATA" ? @"C:\L" : null;
        string? Xdg(string name) => name == "XDG_CONFIG_HOME" ? @"C:\X" : @"C:\L";

        Assert.Equal(@"C:\L\vsneo", NvimLaunch.ConfigDirectory("", Local));
        Assert.Equal(@"C:\X\vsneo", NvimLaunch.ConfigDirectory(null, Xdg));
        Assert.Equal(@"D:\dots\work", NvimLaunch.ConfigDirectory(@"D:\dots\work\", Local));
    }

    [Fact]
    public void Arguments_keep_norc_and_set_the_guard_before_any_config()
    {
        string args = NvimLaunch.Arguments("vsneo-x");

        Assert.Contains("-u NORC", args);
        Assert.Contains("\"let g:vsneo=1\"", args);
        Assert.EndsWith(@"--listen \\.\pipe\vsneo-x", args);
    }
}
