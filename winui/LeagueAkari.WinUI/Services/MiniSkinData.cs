using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public sealed record MiniOwnedSkin(int Id, string Name, string ImagePath, bool Selected, bool Enabled);

public static class MiniSkinData
{
    private static JsonElement Field(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value : default;
    public static MiniOwnedSkin[] Owned(JsonElement snapshot)
    {
        var values = Field(snapshot, "Skins");
        if (values.ValueKind != JsonValueKind.Array) return [];
        var canSelect = Field(snapshot, "CanSelectSkin").ValueKind == JsonValueKind.True;
        return values.EnumerateArray().Select(skin => new MiniOwnedSkin(
            Field(skin, "ID").ValueKind == JsonValueKind.Number && Field(skin, "ID").TryGetInt32(out var id) ? id : 0,
            Field(skin, "Name").ValueKind == JsonValueKind.String ? Field(skin, "Name").GetString() ?? "" : "",
            Field(skin, "ImagePath").ValueKind == JsonValueKind.String ? Field(skin, "ImagePath").GetString() ?? "" : "",
            Field(skin, "Selected").ValueKind == JsonValueKind.True,
            canSelect && Field(skin, "Enabled").ValueKind == JsonValueKind.True
        )).Where(s => s.Id > 0).DistinctBy(s => s.Id).ToArray();
    }
    public static int Columns(double availableWidth) => Math.Clamp((int)Math.Floor(Math.Max(86, availableWidth) / 94), 1, 12);
}
