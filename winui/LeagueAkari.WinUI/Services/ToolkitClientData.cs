using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public record ToolkitNativeRequirement(bool Available, bool NeedsElevation, bool AvailableOnPlatform);
public static class ToolkitClientData
{
    public static ToolkitNativeRequirement Requirement(JsonElement app, string name)
    {
        var value = app.Field("nativeSupport").Field(name);
        return new(value.Boolean("available"), value.Boolean("requiresElevation") && !app.Boolean("isElevated"), value.Boolean("availableOnCurrentPlatform"));
    }
    public static string FileMode(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is "readonly" or "writable" ? value.GetString()! : "unavailable";
}
