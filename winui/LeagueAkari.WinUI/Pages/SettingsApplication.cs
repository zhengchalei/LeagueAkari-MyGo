using System.Text.Json;
using System.Text.Json.Nodes;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

public sealed partial class SettingsPage
{
    private UIElement ConnectionSettings()
    {
        var autoConnect = _forms.Toggle("league-client-main", "autoConnect", "", true);
        var useWmi = _forms.Toggle("league-client-ux-main", "useWmi", "");
        var rebuild = _forms.Action("", "league-client-ux-main", "rebuildWmi");
        var descriptions = Enumerable.Range(0, 3).Select(_ => new TextBlock { TextWrapping = TextWrapping.Wrap }).ToArray();
        var panel = SettingsForms.Section("", autoConnect, descriptions[0], useWmi, descriptions[1], rebuild, descriptions[2]);
        void Render()
        {
            string K(string key) => Localization.Key("settings.app.lcConnection." + key);
            ((TextBlock)panel.Children[0]).Text = K("title");
            autoConnect.Header = K("autoConnect.label"); descriptions[0].Text = K("autoConnect.description");
            useWmi.Header = K("useWmi.label"); descriptions[1].Text = K("useWmi.description");
            rebuild.Content = K("rebuildWmi.rebuildButton"); descriptions[2].Text = K("rebuildWmi.description");
        }
        panel.Loaded += (_, _) => { Localization.Changed += Render; Render(); };
        panel.Unloaded += (_, _) => Localization.Changed -= Render;
        Render(); return panel;
    }

    private UIElement BackgroundChoice()
    {
        var choice = new ComboBox { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        var skin = new ComboBoxItem { Tag = "profile-skin" };
        var plain = new ComboBoxItem { Tag = "none" };
        var mica = new ComboBoxItem { Tag = "mica", IsEnabled = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported() };
        choice.Items.Add(skin); choice.Items.Add(plain); choice.Items.Add(mica);
        bool applying = false, saving = false;
        void Render()
        {
            choice.Header = Localization.Key("settings.app.mainWindowUi.background.label");
            skin.Content = Localization.Key("settings.app.mainWindowUi.background.options.profileSkin");
            plain.Content = Localization.Key("settings.app.mainWindowUi.background.options.none");
            mica.Content = Localization.Key("settings.app.mainWindowUi.background.options.mica");
            ToolTipService.SetToolTip(skin, Localization.Key("settings.app.mainWindowUi.background.tooltips.profileSkin"));
            ToolTipService.SetToolTip(mica, Localization.Key("settings.app.mainWindowUi.background.tooltips." + (mica.IsEnabled ? "mica" : "micaUnsupported")));
            string mode = ApplicationSettingsData.BackgroundMode(_forms.Value("window-manager-main", "backgroundMaterial")?.ToString() ?? "none", _forms.Value("main-window-ui-renderer", "useProfileSkinAsBackground")?.GetValue<bool>() == true);
            applying = true; try { choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == mode); } finally { applying = false; }
        }
        choice.SelectionChanged += async (_, _) =>
        {
            if (applying || saving || choice.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
            saving = true; choice.IsEnabled = false;
            try { foreach (var write in ApplicationSettingsData.BackgroundWrites(mode)) await _forms.Save(write.Namespace, write.Key, JsonSerializer.SerializeToNode(write.Value)); }
            catch { /* Save already displays the backend failure. Render the state that was actually saved. */ }
            finally { saving = false; choice.IsEnabled = true; Render(); }
        };
        _forms.ObserveValue(choice, "window-manager-main", "backgroundMaterial", _ => { if (!saving) Render(); });
        _forms.ObserveValue(choice, "main-window-ui-renderer", "useProfileSkinAsBackground", _ => { if (!saving) Render(); });
        choice.Loaded += (_, _) => { Localization.Changed += Render; Render(); };
        choice.Unloaded += (_, _) => Localization.Changed -= Render;
        Render(); return choice;
    }

    private UIElement UpdateSettings()
    {
        var panel = SettingsForms.Section(Localization.Key("settings.app.selfUpdate.title"),
            _forms.Toggle("remote-config-main", "updateLatestRelease", "自动获取最新发行版", true),
            _forms.Toggle("self-update-main", "autoDownloadUpdates", "自动下载更新"),
            _forms.Choice("remote-config-main", "preferredSource", "远程配置源", ("gitee", "Gitee"), ("github", "GitHub")));
        var buttons = new WrapPanel { Spacing = 8 };
        var check = new Button(); var release = new Button(); var download = new Button(); var cancel = new Button(); var openDirectory = new Button();
        foreach (var button in new[] { check, release, download, cancel, openDirectory }) buttons.Children.Add(button);
        var version = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var lastChecked = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progressText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var progressBar = new ProgressBar { Maximum = 1, MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(buttons); panel.Children.Add(version); panel.Children.Add(lastChecked); panel.Children.Add(progressText); panel.Children.Add(progressBar);
        panel.Children.Add(_forms.Text("self-update-main", "ignoreVersion", "忽略的版本"));
        panel.Children.Add(SourceLatency());
        JsonElement remote = default, updater = default;
        bool loaded = false, checking = false, busy = false;
        long revision = 0;
        string K(string suffix, params (string Key, object? Value)[] values) => Localization.Key("settings.app.selfUpdate." + suffix, null, values.ToDictionary(v => v.Key, v => v.Value));
        void Render()
        {
            var state = ApplicationSettingsData.Update(remote, updater, checking || remote.Boolean("isUpdatingLatestRelease"), busy);
            check.Content = K("checkUpdates"); check.IsEnabled = loaded && state.CanCheck;
            release.Content = K(state.IsNewRelease ? "newRelease" : "currentRelease"); release.Visibility = state.HasRelease ? Visibility.Visible : Visibility.Collapsed;
            download.Content = K("downloadRelease"); download.Visibility = state.IsNewRelease ? Visibility.Visible : Visibility.Collapsed; download.IsEnabled = loaded && state.CanDownload;
            cancel.Content = K("cancelUpdate"); cancel.Visibility = state.IsUpdating ? Visibility.Visible : Visibility.Collapsed;
            // Cancellation stays available while startUpdate waits for download completion.
            cancel.IsEnabled = loaded;
            openDirectory.Content = K("updateDir.label"); openDirectory.Visibility = state.CanOpenDirectory ? Visibility.Visible : Visibility.Collapsed;
            version.Text = state.HasRelease ? remote.Field("latestRelease").Text("version") : "";
            lastChecked.Visibility = state.LastCheckAt > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (state.LastCheckAt > 0) lastChecked.Text = K("lastCheckAt") + ": " + DateTimeOffset.FromUnixTimeMilliseconds((long)state.LastCheckAt).ToLocalTime().ToString("g", Localization.IsEnglish ? System.Globalization.CultureInfo.GetCultureInfo("en-US") : System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
            progressText.Visibility = progressBar.Visibility = state.IsUpdating ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = state.Progress;
            progressText.Text = state.Phase switch
            {
                "downloading" => K("updateProgress.downloading") + " · " + K("updateProgress.finished", ("progress", (state.Progress * 100).ToString("F0"))) + " · " + K("updateProgress.remain", ("time", HostNotificationRules.FormatSeconds(state.SecondsLeft))),
                "download-failed" => K("updateProgress.downloadFailed"),
                "waiting-for-restart" => K("updateProgress.waitingForRestart") + "\n" + K("updateProgress.waitingForRestartDescription"),
                _ => ""
            };
        }
        async Task Refresh()
        {
            long before = revision;
            var values = await Task.WhenAll(_backend.StateAsync("remote-config-main"), _backend.StateAsync("self-update-main"));
            if (!loaded) return;
            if (before == revision) { remote = values[0]; updater = values[1]; }
            Render();
        }
        void OnEvent(JsonElement update)
        {
            string name = update.Text("name");
            if (name is not ("update-state-prop/remote-config-main:state" or "update-state-prop/self-update-main:state")) return;
            var args = update.Field("args").Items().ToArray(); if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String) return;
            panel.DispatcherQueue.TryEnqueue(() =>
            {
                if (!loaded) return;
                bool isRemote = name == "update-state-prop/remote-config-main:state";
                var previous = isRemote ? remote : updater;
                var copy = previous.ValueKind == JsonValueKind.Object ? previous.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : [];
                copy[args[0].GetString()!] = args[1].Clone();
                if (isRemote) remote = JsonSerializer.SerializeToElement(copy); else updater = JsonSerializer.SerializeToElement(copy);
                revision++; Render();
            });
        }
        check.Click += async (_, _) =>
        {
            checking = true; Render();
            try
            {
                var result = await _backend.CallAsync("self-update-main", "checkUpdates");
                _forms.Status.Text = Localization.Key(ApplicationSettingsData.CheckResultKey(result), null, new Dictionary<string, object?> { ["reason"] = result.Text("reason") });
                await Refresh();
            }
            catch (Exception error) { _forms.Status.Text = K("checkUpdatesResult.failed", ("reason", error.Message)); }
            finally { checking = false; if (loaded) Render(); }
        };
        release.Click += async (_, _) => { try { if (_showRelease != null) await _showRelease(); } catch (Exception error) { _forms.Status.Text = error.Message; } };
        download.Click += async (_, _) =>
        {
            busy = true; Render();
            try
            {
                var result = await _backend.CallAsync("self-update-main", "startUpdate");
                if (result.Text("result") == "failed") _forms.Status.Text = Localization.Text("更新失败：", "Update failed: ") + result.Text("reason");
                await Refresh();
            }
            catch (Exception error) { _forms.Status.Text = error.Message; }
            finally { busy = false; if (loaded) Render(); }
        };
        cancel.Click += async (_, _) => { try { await _backend.CallAsync("self-update-main", "cancelUpdate"); await Refresh(); } catch (Exception error) { _forms.Status.Text = error.Message; } };
        openDirectory.Click += async (_, _) => { try { await _backend.CallAsync("self-update-main", "openNewUpdatesDir"); } catch (Exception error) { _forms.Status.Text = error.Message; } };
        panel.Loaded += async (_, _) => { loaded = true; _backend.EventReceived -= OnEvent; _backend.EventReceived += OnEvent; Localization.Changed += Render; try { await Refresh(); } catch (Exception error) { _forms.Status.Text = error.Message; } };
        panel.Unloaded += (_, _) => { loaded = false; revision++; _backend.EventReceived -= OnEvent; Localization.Changed -= Render; };
        Render(); return panel;
    }

    private UIElement SourceLatency()
    {
        var panel = new StackPanel { Spacing = 6 };
        var button = new Button(); var gitee = new TextBlock { TextWrapping = TextWrapping.Wrap }; var github = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(button); panel.Children.Add(gitee); panel.Children.Add(github);
        bool testing = false, active = false, hasResult = false; double giteeLatency = 0, githubLatency = 0; long revision = 0;
        string K(string key) => Localization.Key("settings.app.basic.dataSource." + key);
        void Render()
        {
            button.Content = K("testButton"); button.IsEnabled = !testing;
            string Value(double latency, double other) => testing ? K("testingSpeed") : !hasResult ? "" : "(" + (latency == -1 ? K("timeout") : latency.ToString("F1") + " ms") + ")" + (ApplicationSettingsData.FasterSource(latency, other) ? " " + K("better") : "");
            gitee.Text = "Gitee " + Value(giteeLatency, githubLatency) + "\n" + K("tip.gitee");
            github.Text = "GitHub " + Value(githubLatency, giteeLatency) + "\n" + K("tip.github");
        }
        async Task Test()
        {
            if (testing) return; testing = true; long current = revision; Render();
            try { var result = await _backend.CallAsync("remote-config-main", "testRepoLatency"); if (active && current == revision) { giteeLatency = result.Number("giteeLatency", -1); githubLatency = result.Number("githubLatency", -1); hasResult = true; } }
            catch (Exception error) { if (active && current == revision) _forms.Status.Text = error.Message; }
            finally { testing = false; if (active) Render(); }
        }
        button.Click += async (_, _) => await Test();
        panel.Loaded += async (_, _) => { active = true; revision++; Localization.Changed += Render; Render(); if (!hasResult) await Test(); };
        panel.Unloaded += (_, _) => { active = false; revision++; Localization.Changed -= Render; };
        Render(); return panel;
    }
}
