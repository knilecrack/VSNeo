using System.Text.Json;
using VSNeo.Seeky;
using Xunit;

namespace VSNeo.Tests;

public class SeekyAppearanceSettingsTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"blurBackground\":true}", true)]
    [InlineData("{\"blurBackground\":false}", false)]
    [InlineData("{\"blurBackground\":1}", false)]
    [InlineData("{\"blurBackground\":\"true\"}", false)]
    public void Blur_background_is_opt_in_and_must_be_a_json_boolean(string json, bool expected)
    {
        Assert.Equal(expected, SeekyAppearanceSettings.ReadBlurBackground(json));
    }

    [Fact]
    public void Malformed_settings_json_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => SeekyAppearanceSettings.ReadBlurBackground("{"));
    }
}
