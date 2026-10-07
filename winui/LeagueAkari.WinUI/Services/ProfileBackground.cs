using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Services;

public sealed class ProfileBackground(BackendClient backend)
{
    public Image Image { get; } = new() { Stretch = Stretch.UniformToFill, Opacity = .10, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly NativeImages _images = new(backend);
    private int _revision;
    public async Task RefreshAsync(PlayerDataSource source, string puuid)
    {
        int revision = ++_revision;
        Image.Visibility = Visibility.Collapsed;
        Image.Source = null;
        try
        {
            var enabled = await backend.CallAsync("setting-factory-main", "get", "main-window-ui-renderer", "useProfileSkinAsBackground");
            if (revision != _revision || enabled.ValueKind != JsonValueKind.True || !source.SupportsSocialProfile) return;
            var material = await backend.CallAsync("setting-factory-main", "get", "window-manager-main", "backgroundMaterial");
            if (revision != _revision || material.ValueKind == JsonValueKind.String && material.GetString() == "mica" && Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) return;
            var profile = await source.SocialProfileAsync(puuid);
            int skin = (int)profile.Number("backgroundSkinId");
            int champion = skin / 1000;
            if (champion <= 0)
            {
                var mastery = await source.MasteryAsync(puuid, 1);
                champion = (int)mastery.Field("masteries").Items().FirstOrDefault().Number("championId");
                skin = champion * 1000;
            }
            if (champion <= 0) return;
            var details = await source.LcuAsync("GET", $"/lol-game-data/assets/v1/champions/{champion}.json");
            var skins = details.Field("skins").Items().ToArray();
            var entry = skins.FirstOrDefault(s => s.Number("id") == skin);
            if (entry.ValueKind == JsonValueKind.Undefined)
                entry = skins.SelectMany(s => s.Field("questSkinInfo").Field("tiers").Items()).FirstOrDefault(s => s.Number("id") == skin);
            string path = entry.Text("splashPath");
            if (path.Length == 0 || revision != _revision) return;
            Image.Width = Math.Max(800, Image.ActualWidth);
            await _images.SetAsync(Image, path);
            if (revision == _revision) Image.Visibility = Visibility.Visible;
        }
        catch { /* A failed optional background must not replace usable match data. */ }
    }
}
