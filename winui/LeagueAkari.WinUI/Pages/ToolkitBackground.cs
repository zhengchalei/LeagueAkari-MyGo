using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class ToolkitPage
{
    private StackPanel BackgroundTools()
    {
        var panel = Panel(); panel.Children.Add(Label("个人资料背景"));
        var championSearch = new TextBox { PlaceholderText = "搜索英雄" };
        var champions = new ComboBox { Header = "英雄", MinWidth = 240 };
        var skinSearch = new TextBox { PlaceholderText = "搜索皮肤" };
        var skins = new ComboBox { Header = "皮肤", MinWidth = 300 };
        var augments = new ComboBox { Header = "皮肤背景形态", MinWidth = 300, Visibility = Visibility.Collapsed };
        var preview = new Grid { Width = 350, Height = 190, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
        var images = new NativeImages(_backend); JsonElement[] catalog = []; ProfileBackgroundSkin[] choices = [];
        int request = 0, previewRequest = 0; bool loading = false;
        Button? apply = null;
        string? SelectedAugment() => augments.SelectedItem is ComboBoxItem { Tag: string value } ? value : null;
        void RenderPreview()
        {
            int version = ++previewRequest; preview.Children.Clear();
            if (skins.SelectedItem is not ComboBoxItem { Tag: ProfileBackgroundSkin skin } || skin.SplashPath.Length == 0) { preview.Visibility = Visibility.Collapsed; return; }
            preview.Visibility = Visibility.Visible;
            var paths = new[] { skin.SplashPath }.Concat(skin.Augments.FirstOrDefault(augment => augment.ContentId == SelectedAugment())?.OverlayPaths ?? []);
            foreach (string path in paths)
            {
                var image = new Image { Width = 350, Height = 190, Stretch = Stretch.UniformToFill }; preview.Children.Add(image);
                _ = Load(image, path, version);
            }
        }
        async Task Load(Image image, string path, int version) { await images.SetAsync(image, path); if (version != previewRequest) image.Source = null; }
        void DrawSkins()
        {
            int? previous = skins.SelectedItem is ComboBoxItem { Tag: ProfileBackgroundSkin skin } ? skin.Id : null;
            skins.Items.Clear();
            foreach (var option in choices.Where(option => option.Name.Contains(skinSearch.Text, StringComparison.CurrentCultureIgnoreCase) || option.Id.ToString().Contains(skinSearch.Text)))
            {
                var item = new ComboBoxItem { Content = option.Name, Tag = option }; skins.Items.Add(item); if (option.Id == previous) skins.SelectedItem = item;
            }
            if (skins.SelectedItem is null && skins.Items.Count > 0) skins.SelectedIndex = 0;
            if (apply is not null) apply.IsEnabled = !loading && skins.SelectedItem is ComboBoxItem;
        }
        void DrawChampions()
        {
            int? previous = champions.SelectedItem is ComboBoxItem { Tag: int previousId } ? previousId : null;
            champions.Items.Clear(); foreach (var champion in catalog.Where(champion => (champion.Text("name") + champion.Number("id")).Contains(championSearch.Text, StringComparison.CurrentCultureIgnoreCase)).OrderBy(champion => champion.Text("name")))
            {
                int championId = (int)champion.Number("id"); var item = new ComboBoxItem { Content = champion.Text("name"), Tag = championId }; champions.Items.Add(item); if (previous == championId) champions.SelectedItem = item;
            }
            if (champions.SelectedItem is null && champions.Items.Count > 0) champions.SelectedIndex = 0;
        }
        champions.SelectionChanged += async (_, _) =>
        {
            int version = ++request; loading = false; choices = []; skins.Items.Clear(); augments.Items.Clear(); preview.Children.Clear(); preview.Visibility = Visibility.Collapsed;
            if (apply is not null) apply.IsEnabled = false;
            if (champions.SelectedItem is not ComboBoxItem { Tag: int id }) return;
            loading = true;
            try
            {
                var detail = await Lcu("GET", $"/lol-game-data/assets/v1/champions/{id}.json");
                if (version != request || champions.SelectedItem is not ComboBoxItem { Tag: int selected } || selected != id || detail.Number("id") != id) return;
                choices = ProfileBackgroundData.Read(detail); DrawSkins();
            }
            catch (Exception ex) { if (version == request) _status.Text = ex.Message; }
            finally { if (version == request) { loading = false; if (apply is not null) apply.IsEnabled = skins.SelectedItem is ComboBoxItem; } }
        };
        skins.SelectionChanged += (_, _) =>
        {
            augments.Items.Clear(); augments.Visibility = Visibility.Collapsed;
            if (skins.SelectedItem is ComboBoxItem { Tag: ProfileBackgroundSkin skin } && skin.Augments.Length > 0)
            {
                augments.Visibility = Visibility.Visible; augments.Items.Add(new ComboBoxItem { Content = "不设置形态", Tag = "" });
                foreach (var augment in skin.Augments) augments.Items.Add(new ComboBoxItem { Content = Localization.Key("toolkit.summonerProfile.skinSelectModal.augment", "Augment") + " " + augment.ContentId, Tag = augment.ContentId });
            }
            if (apply is not null) apply.IsEnabled = !loading && skins.SelectedItem is ComboBoxItem;
            RenderPreview();
        };
        augments.SelectionChanged += (_, _) => RenderPreview();
        championSearch.TextChanged += (_, _) => DrawChampions(); skinSearch.TextChanged += (_, _) => DrawSkins();
        var refresh = Action("读取英雄列表", async () =>
        {
            var data = (await _backend.StateAsync("league-client-main", "gameData")).Field("champions");
            catalog = (data.ValueKind == JsonValueKind.Object ? data.EnumerateObject().Select(item => item.Value) : data.Items()).Where(champion => champion.Number("id") > 0).ToArray(); DrawChampions();
        });
        apply = Action("应用资料背景", async () =>
        {
            if (loading || skins.SelectedItem is not ComboBoxItem { Tag: ProfileBackgroundSkin skin }) throw new InvalidOperationException(Localization.Text("请选择皮肤", "Select a skin"));
            var writes = ProfileBackgroundData.Writes(skin, SelectedAugment());
            foreach (var control in new Control[] { champions, championSearch, skins, skinSearch, augments }) control.IsEnabled = false;
            try
            {
                var state = await _backend.StateAsync("league-client-main"); if (state.Text("connectionState") != "connected") throw new InvalidOperationException(Localization.Text("未连接 LOL 客户端", "League client is disconnected"));
                foreach (var write in writes) await Lcu("POST", "/lol-summoner/v1/current-summoner/summoner-profile", new { key = write.Key, value = write.Value });
            }
            finally { foreach (var control in new Control[] { champions, championSearch, skins, skinSearch, augments }) control.IsEnabled = true; }
        }); apply.IsEnabled = false;
        panel.Children.Add(refresh); panel.Children.Add(championSearch); panel.Children.Add(champions); panel.Children.Add(skinSearch); panel.Children.Add(skins); panel.Children.Add(augments); panel.Children.Add(preview); panel.Children.Add(apply);
        panel.Unloaded += (_, _) => { request++; previewRequest++; loading = false; };
        return panel;
    }
}
