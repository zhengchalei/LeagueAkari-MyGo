using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record ProfileBackgroundAugment(string ContentId, string[] OverlayPaths);
public sealed record ProfileBackgroundSkin(int Id, string Name, string SplashPath, ProfileBackgroundAugment[] Augments);

/// <summary>The original toolkit's base skins and quest tiers; chromas are not background options.</summary>
public static class ProfileBackgroundData
{
    public static ProfileBackgroundSkin[] Read(JsonElement champion)
    {
        var skins = new List<ProfileBackgroundSkin>(); var ids = new HashSet<int>();
        void Add(JsonElement skin)
        {
            int id = (int)skin.Number("id"); if (id <= 0 || !ids.Add(id)) return;
            var augments = skin.Field("skinAugments").Field("augments").Items().Where(augment => augment.Field("overlays").ValueKind == JsonValueKind.Array)
                .Select(augment => new ProfileBackgroundAugment(augment.Text("contentId"), augment.Field("overlays").Items().Select(overlay => overlay.Text("uncenteredLCOverlayPath")).Where(path => path.Length > 0).ToArray())).ToArray();
            skins.Add(new(id, skin.Text("name", id.ToString()), skin.Text("uncenteredSplashPath"), augments));
        }
        foreach (var skin in champion.Field("skins").Items()) { Add(skin); foreach (var tier in skin.Field("questSkinInfo").Field("tiers").Items()) Add(tier); }
        return skins.ToArray();
    }
    public static (string Key, object Value)[] Writes(ProfileBackgroundSkin skin, string? augment)
    {
        if (augment is not null && augment.Length > 0 && !skin.Augments.Any(option => option.ContentId == augment)) throw new InvalidOperationException("Unknown background augment");
        return augment is null ? [("backgroundSkinId", skin.Id)] : [("backgroundSkinId", skin.Id), ("backgroundSkinAugments", augment)];
    }
}
