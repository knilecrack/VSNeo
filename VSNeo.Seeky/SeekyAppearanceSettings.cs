
using System.Text.Json;

namespace VSNeo.Seeky;

internal static class SeekyAppearanceSettings
{
    internal static bool ReadBlurBackground(string json)
    {
        using JsonDocument settings = JsonDocument.Parse(json);
        return settings.RootElement.TryGetProperty("blurBackground", out JsonElement element)
            && element.ValueKind == JsonValueKind.True;
    }
}
