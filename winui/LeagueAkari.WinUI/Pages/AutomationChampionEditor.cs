using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class AutomationPage
{
    private async Task<UIElement> ChampionEditor()
    {
        const string ns = "auto-champ-config-main";
        string T(string key) => Localization.Key("automation.champConfig.championConfig." + key);
        var panel = new StackPanel { Spacing = 12 }; var enabled = _forms.Toggle(ns, "enabled", Localization.Key("automation.champConfig.enabled.label")); panel.Children.Add(enabled);
        var description = new TextBlock { Text = Localization.Key("automation.champConfig.enabled.description"), TextWrapping = TextWrapping.Wrap }; panel.Children.Add(description);
        var raw = await _backend.StateAsync("league-client-main", "gameData");
        var data = JsonNode.Parse(raw.GetRawText())?.AsObject() ?? new();
        var champions = Catalog(data["champions"]); var spells = Catalog(data["summonerSpells"]);
        JsonElement extra = default;
        try { extra = await _backend.StateAsync("extra-assets-main", "gtimg"); } catch { }
        var searchChampions = AutoSelectData.Champions(raw.Field("champions"), extra).ToDictionary(c => c.Id);
        var assets = new GameAssets(_backend);
        var images = new NativeImages(_backend);
        var hero = new ComboBox { Header = T("configure"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var search = new TextBox { PlaceholderText = T("searchPlaceholder") };
        var mode = SettingsForms.Select(T("targetMode"), ChampionConfigEditorData.Modes.Select(key => (key, T(key))));
        var position = SettingsForms.Select(T("position"), Positions.Select(p => (p.Value, p.Value == "default" ? T("default") : Localization.Key("positions." + p.Value, p.Label))));
        var kind = SettingsForms.Select(T("configure"), [("runes", T("runes")), ("spells", T("spells"))]);
        panel.Children.Add(search); panel.Children.Add(hero); panel.Children.Add(mode); panel.Children.Add(kind); panel.Children.Add(position);
        var editor = new StackPanel { Spacing = 10 }; var editorHost = new ContentControl { Content = editor }; panel.Children.Add(editorHost);
        var writer = new ChampionConfigWriter((space, method, args) => _backend.CallAsync(space, method, args));
        bool attached = false, rebinding = false, saving = false; int revision = 0, resourcesRevision = 0;
        ChampionConfigDraft? currentDraft = null; int currentHero = 0; string currentType = "", currentPosition = "", currentKind = "";
        bool Exists(string field, int id, string? key = null) => key is null ? _forms.Value(ns, field + "." + id) is JsonObject config && config.Any(p => p.Value != null) : _forms.Value(ns, field + "." + id + "." + key) != null;
        bool InMode(string field, int id, string target) => target == "ranked" ? (_forms.Value(ns, field + "." + id) as JsonObject)?.Any(p => p.Key.StartsWith("ranked") && p.Value != null) == true : Exists(field, id, target);
        void RefreshOptions(ComboBox control, Func<string, string> label)
        {
            string selected = (control.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            string[] keys = control.Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString() ?? "").ToArray(); control.Items.Clear();
            foreach (string target in keys) { var item = new ComboBoxItem { Tag = target, Content = label(target) }; control.Items.Add(item); if (target == selected) control.SelectedItem = item; }
        }
        void RefreshChampions()
        {
            rebinding = true; string? selected = (hero.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? (currentHero > 0 ? currentHero.ToString() : null);
            hero.Items.Clear();
            foreach (var c in ChampionConfigEditorData.Sort(champions.Select(c => new ChampionConfigListEntry(c.ID, c.Name, Exists("runesV2", c.ID), Exists("summonerSpells", c.ID)))).Where(c => c.Id.ToString() == selected || AutoSelectData.Matches(searchChampions.GetValueOrDefault(c.Id, new(c.Id, c.Name, "")), search.Text, null, default)))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; row.Children.Add(assets.Icon("champion-summary", c.Id, 20)); row.Children.Add(new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center });
                var config = new TextBlock { Text = (c.Runes ? " ◉" : "") + (c.Spells ? " ⚡" : "") }; ToolTipService.SetToolTip(config, T(c.Runes ? "runesConfigured" : "runesUnconfigured") + "\n" + T(c.Spells ? "spellsConfigured" : "spellsUnconfigured")); row.Children.Add(config);
                hero.Items.Add(new ComboBoxItem { Tag = c.Id.ToString(), Content = row });
            }
            hero.SelectedItem = hero.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == selected) ?? hero.Items.OfType<ComboBoxItem>().FirstOrDefault();
            rebinding = false;
        }
        search.TextChanged += (_, _) => RefreshChampions();
        void Render(bool reset = true)
        {
            if (rebinding || !SettingsForms.TrySelected(mode, out string type) || !SettingsForms.TrySelected(position, out string selectedPosition) || !SettingsForms.TrySelected(kind, out string selectedKind)) return;
            int transaction = ++revision;
            editor.Children.Clear();
            if (!SettingsForms.TrySelected(hero, out string selectedHero) || !int.TryParse(selectedHero, out int id)) { editor.Children.Add(new TextBlock { Text = T("noChampionPlaceholder") }); return; }
            position.Visibility = type == "ranked" ? Visibility.Visible : Visibility.Collapsed;
            string key = ChampionConfigDraft.StorageKey(type, selectedPosition);
            rebinding = true;
            RefreshOptions(mode, target => T(target) + (InMode("runesV2", id, target) || InMode("summonerSpells", id, target) ? " ✓" : ""));
            RefreshOptions(kind, target => T(target) + (InMode(target == "runes" ? "runesV2" : "summonerSpells", id, type) ? " ✓" : ""));
            RefreshOptions(position, target => (target == "default" ? T("default") : Localization.Key("positions." + target)) + (Exists(selectedKind == "runes" ? "runesV2" : "summonerSpells", id, ChampionConfigDraft.StorageKey("ranked", target)) ? " ✓" : ""));
            rebinding = false;
            JsonElement Read(string field) => _forms.Value(ns, $"{field}.{id}.{key}") is { } node ? JsonSerializer.SerializeToElement(node) : default;
            var draft = !reset && currentDraft != null && currentHero == id && currentType == type && currentPosition == selectedPosition && currentKind == selectedKind ? currentDraft : new ChampionConfigDraft(raw.Field("perkstyles"), ChampionConfigDraft.ReadRunes(Read("runesV2")), ChampionConfigDraft.ReadSpells(Read("summonerSpells")));
            currentDraft = draft; currentHero = id; currentType = type; currentPosition = selectedPosition; currentKind = selectedKind;
            string gameMode = ChampionConfigEditorData.GameMode(type);
            var available = spells.Where(s => s.Data["gameModes"] is JsonArray modes && modes.Any(m => m?.ToString() == gameMode)).ToArray();
            draft.NormalizeSpells(available.Select(s => s.ID));
            var spellEditor = new StackPanel { Spacing = 8 }; var runeEditor = new StackPanel { Spacing = 8 }; editor.Children.Add(SettingsForms.Section(T(selectedKind == "spells" ? "spells" : "runes"), selectedKind == "spells" ? spellEditor : runeEditor));
            async Task SaveAsync(bool runes, Button button)
            {
                if (saving) return;
                if (runes ? !draft.ValidRunes() : !draft.ValidSpells(available.Select(s => s.ID))) return;
                bool written = false; saving = true; button.IsEnabled = false; hero.IsEnabled = mode.IsEnabled = position.IsEnabled = kind.IsEnabled = search.IsEnabled = editorHost.IsEnabled = false;
                try { written = await writer.SaveAsync(id, type, selectedPosition, runes, draft, available.Select(s => s.ID)); if (!written || !attached) return; await _forms.Load(ns); if (attached && transaction == revision) _forms.Status.Text = T(runes ? "runesSaved" : "spellsSaved"); }
                catch (Exception failure) { if (attached && transaction == revision) _forms.Status.Text = failure.Message; }
                finally { saving = false; hero.IsEnabled = mode.IsEnabled = position.IsEnabled = kind.IsEnabled = search.IsEnabled = editorHost.IsEnabled = true; if (attached) { RefreshChampions(); Render(written); } }
            }
            Button Action(Panel parent, string label, Action action, bool enabled = true) { var button = new Button { Content = label, IsEnabled = enabled }; button.Click += (_, _) => { if (saving) return; action(); }; parent.Children.Add(button); return button; }
            void Toolbar(Panel parent, bool runes)
            {
                var bar = new WrapPanel { Spacing = 6 }; parent.Children.Add(bar);
                bool exists = runes ? draft.Runes != null : draft.Spells != null, changed = runes ? draft.RunesChanged : draft.SpellsChanged;
                Action(bar, T("clear"), () => { if (runes) { draft.ClearRunes(); DrawRunes(); } else { draft.ClearSpells(); DrawSpells(); } }, exists && !saving);
                Action(bar, T("restore"), () => { if (runes) { draft.RestoreRunes(); DrawRunes(); } else { draft.RestoreSpells(); draft.NormalizeSpells(available.Select(s => s.ID)); DrawSpells(); } _forms.Status.Text = T(runes ? "runesRestored" : "spellsRestored"); }, changed && !saving);
                var save = new Button { Content = T("save"), IsEnabled = changed && !saving && (runes ? draft.ValidRunes() : draft.ValidSpells(available.Select(s => s.ID))) }; bar.Children.Add(save); save.Click += async (_, _) => await SaveAsync(runes, save);
            }
            void DrawSpells()
            {
                spellEditor.Children.Clear();
                if (draft.Spells is not { } current) Action(spellEditor, T("configureSpells"), () => { draft.CreateSpells(available.Select(s => s.ID)); DrawSpells(); });
                else
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    foreach (bool first in new[] { true, false })
                    {
                        var pick = new Button { Content = assets.Icon("summoner-spells", first ? current.Spell1Id : current.Spell2Id, 32), Padding = new Thickness(2), IsEnabled = !saving };
                        var choices = new WrapPanel { Spacing = 6, MaxWidth = 320 };
                        foreach (var spell in available) { var item = new Button { Content = assets.Icon("summoner-spells", spell.ID, 32), Padding = new Thickness(2) }; ToolTipService.SetToolTip(item, spell.Name); item.Click += (_, _) => { if (saving) return; draft.SelectSpell(first, spell.ID, available.Select(s => s.ID)); ((Flyout)pick.Flyout).Hide(); DrawSpells(); }; choices.Children.Add(item); }
                        pick.Flyout = new Flyout { Content = choices }; row.Children.Add(pick);
                    }
                    spellEditor.Children.Add(row);
                }
                Toolbar(spellEditor, false);
            }
            Button RuneButton(Panel row, string catalog, int perkId, bool selected, Action click, bool darken = false, int size = 32)
            {
                var resource = catalog == "perkstyles" ? draft.Style(perkId) : HistoryCardData.Resource(raw, catalog, perkId);
                var icon = new Image { Width = catalog == "perkstyles" ? 28 : size, Height = catalog == "perkstyles" ? 28 : size, Opacity = darken ? .35 : 1 };
                string path = resource.Text("iconPath"); icon.Loaded += async (_, _) => { if (path.Length > 0) await images.SetAsync(icon, path); };
                var button = new Button { Content = icon, Padding = new Thickness(2), IsEnabled = !saving, BorderThickness = new Thickness(selected ? 2 : 0), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue) };
                ToolTipService.SetToolTip(button, resource.Text("name") + "\n" + System.Text.RegularExpressions.Regex.Replace(resource.Text("longDesc", resource.Text("description")), "<[^>]+>", " ")); button.Click += (_, _) => { if (!saving) click(); }; row.Children.Add(button); return button;
            }
            void DrawRunes()
            {
                runeEditor.Children.Clear();
                if (!draft.IsSupported) { runeEditor.Children.Add(new TextBlock { Text = Localization.Key("automation.champConfig.runeEditor.unsupported"), TextWrapping = TextWrapping.Wrap }); Toolbar(runeEditor, true); return; }
                if (draft.Runes is not { } current) Action(runeEditor, T("configureRunes"), () => { draft.CreateRunes(); DrawRunes(); });
                else
                {
                    var mainStyles = new WrapPanel { Spacing = 6 }; runeEditor.Children.Add(mainStyles);
                    foreach (var style in draft.Styles) { int styleId = (int)style.Number("id"); RuneButton(mainStyles, "perkstyles", styleId, current.PrimaryStyleId == styleId, () => { draft.SelectPrimary(styleId); DrawRunes(); }); }
                    var columns = new Grid { ColumnSpacing = 22 }; columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new()); var mainPanel = new StackPanel { Spacing = 6 }; var subPanel = new StackPanel { Spacing = 6 }; columns.Children.Add(mainPanel); Grid.SetColumn(subPanel, 1); columns.Children.Add(subPanel); runeEditor.Children.Add(columns);
                    var mainSlots = draft.Slots(current.PrimaryStyleId, "kKeyStone").Concat(draft.Slots(current.PrimaryStyleId, "kMixedRegularSplashable")).ToArray();
                    for (int i = 0; i < mainSlots.Length; i++) { int index = i; var row = new WrapPanel { Spacing = 4 }; var perks = ChampionConfigDraft.Perks(mainSlots[i]); int chosen = current.SelectedPerkIds.ElementAtOrDefault(index); foreach (int perkId in perks) RuneButton(row, "perks", perkId, chosen == perkId, () => { draft.SelectPrimaryPerk(index, perkId); DrawRunes(); }, perks.Contains(chosen) && chosen != perkId, index == 0 ? 36 : 32); mainPanel.Children.Add(row); }
                    var subStyles = new WrapPanel { Spacing = 6 }; subPanel.Children.Add(subStyles); foreach (var item in draft.Style(current.PrimaryStyleId).Field("allowedSubStyles").Items()) { int subId = (int)item.TryNumber(); RuneButton(subStyles, "perkstyles", subId, current.SubStyleId == subId, () => { draft.SelectSecondary(subId); DrawRunes(); }); }
                    var subSlots = draft.Slots(current.SubStyleId, "kMixedRegularSplashable");
                    var subSelected = current.SelectedPerkIds.Skip(4).Take(2).ToArray(); bool twoSubRows = subSlots.Count(slot => ChampionConfigDraft.Perks(slot).Any(subSelected.Contains)) >= 2;
                    for (int i = 0; i < subSlots.Length; i++) { int index = i; var row = new WrapPanel { Spacing = 4 }; var perks = ChampionConfigDraft.Perks(subSlots[i]); foreach (int perkId in perks) RuneButton(row, "perks", perkId, subSelected.Contains(perkId), () => { draft.SelectSecondaryPerk(index, perkId); DrawRunes(); }, !subSelected.Contains(perkId) && (twoSubRows || perks.Any(subSelected.Contains))); subPanel.Children.Add(row); }
                    var shards = draft.Slots(current.PrimaryStyleId, "kStatMod");
                    for (int i = 0; i < shards.Length; i++) { int index = i + 6; var row = new WrapPanel { Spacing = 4 }; int chosen = current.SelectedPerkIds.ElementAtOrDefault(index); foreach (int perkId in ChampionConfigDraft.Perks(shards[i])) RuneButton(row, "perks", perkId, chosen == perkId, () => { draft.SelectPrimaryPerk(index, perkId); DrawRunes(); }, chosen > 0 && chosen != perkId, 22); subPanel.Children.Add(row); }
                }
                Toolbar(runeEditor, true);
            }
            if (selectedKind == "spells") DrawSpells(); else DrawRunes();
        }
        void SettingsChanged(string space, string key)
        {
            if (!attached || space != ns || key is not ("runesV2" or "summonerSpells" or "")) return;
            RefreshChampions(); Render();
        }
        void LanguageChanged()
        {
            panel.DispatcherQueue.TryEnqueue(() =>
            {
                if (!attached) return; rebinding = true;
                enabled.Header = Localization.Key("automation.champConfig.enabled.label"); description.Text = Localization.Key("automation.champConfig.enabled.description");
                hero.Header = T("configure"); search.PlaceholderText = T("searchPlaceholder"); mode.Header = T("targetMode"); kind.Header = T("configure"); position.Header = T("position");
                foreach (ComboBoxItem item in mode.Items) if (item.Tag is string key) item.Content = T(key);
                foreach (ComboBoxItem item in kind.Items) if (item.Tag is string key) item.Content = T(key);
                foreach (ComboBoxItem item in position.Items) if (item.Tag is string key) item.Content = key == "default" ? T("default") : Localization.Key("positions." + key);
                rebinding = false; RefreshChampions(); Render(false);
            });
        }
        async Task ReloadResourcesAsync()
        {
            int request = ++resourcesRevision;
            try
            {
                var resources = await _backend.StateAsync("league-client-main", "gameData");
                var refreshedExtra = extra;
                try { refreshedExtra = await _backend.StateAsync("extra-assets-main", "gtimg"); } catch { }
                if (!attached || request != resourcesRevision) return;
                extra = refreshedExtra;
                raw = resources; data = JsonNode.Parse(raw.GetRawText())?.AsObject() ?? new(); champions = Catalog(data["champions"]); spells = Catalog(data["summonerSpells"]);
                searchChampions = AutoSelectData.Champions(raw.Field("champions"), extra).ToDictionary(c => c.Id); RefreshChampions(); Render();
            }
            catch (Exception error) { if (attached && request == resourcesRevision) _forms.Status.Text = error.Message; }
        }
        void BackendEvent(JsonElement ev)
        {
            string name = ev.Text("name");
            if (name.Contains("league-client-main:gameData") || name.Contains("extra-assets-main:gtimg")) panel.DispatcherQueue.TryEnqueue(async () => await ReloadResourcesAsync());
        }
        panel.Loaded += (_, _) => { if (attached) return; attached = true; _forms.Changed += SettingsChanged; Localization.Changed += LanguageChanged; _backend.EventReceived += BackendEvent; RefreshChampions(); Render(); };
        panel.Unloaded += (_, _) => { attached = false; revision++; resourcesRevision++; _forms.Changed -= SettingsChanged; Localization.Changed -= LanguageChanged; _backend.EventReceived -= BackendEvent; };
        hero.SelectionChanged += (_, _) => Render(); mode.SelectionChanged += (_, _) => Render(); kind.SelectionChanged += (_, _) => Render(); position.SelectionChanged += (_, _) => Render(); RefreshChampions(); Render(); return panel;
    }
}
