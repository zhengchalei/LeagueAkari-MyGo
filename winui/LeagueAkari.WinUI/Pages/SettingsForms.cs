using System.Text.Json.Nodes;
using System.Text.Json;
using LeagueAkari.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAkari.WinUI.Pages;

internal sealed class SettingsForms
{
    private readonly BackendClient _backend;
    private readonly Dictionary<string, JsonObject> _values = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly List<(string Namespace, string Path, Action Update)> _bindings = new();
    private bool _updating;
    private bool _observing;
    public event Action<string,string>? Changed;
    public TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap };
    public SettingsForms(BackendClient backend) => _backend = backend;
    public void Observe()
    {
        if (_observing) return;
        _observing = true; _backend.EventReceived += SettingUpdated;
    }
    public void Release() { _backend.EventReceived -= SettingUpdated; _observing = false; }
    private void Bind(FrameworkElement control, string ns, string path, Action update)
    {
        var binding = (Namespace: ns, Path: path, Update: update);
        control.Loaded += (_, _) => { if (!_bindings.Contains(binding)) _bindings.Add(binding); bool wasUpdating = _updating; _updating = true; try { update(); } finally { _updating = wasUpdating; } };
        control.Unloaded += (_, _) => _bindings.Remove(binding);
    }
    public void ObserveValue(FrameworkElement control, string ns, string path, Action<JsonNode?> update) => Bind(control, ns, path, () => update(Value(ns, path)));
    private void SettingUpdated(JsonElement envelope)
    {
        string name = envelope.Text("name");
        const string prefix = "update-state-prop/";
        if (!name.StartsWith(prefix) || !name.EndsWith(":settings")) return;
        string ns = name[prefix.Length..^":settings".Length];
        var args = envelope.Field("args").Items().ToArray();
        if (args.Length < 2 || args[0].ValueKind != JsonValueKind.String) return;
        string key = args[0].GetString()!;
        Status.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_observing || !_values.ContainsKey(ns)) return;
            _values[ns][key] = JsonNode.Parse(args[1].GetRawText());
            bool wasUpdating = _updating; _updating = true;
            try { foreach (var binding in _bindings.Where(b => b.Namespace == ns && (b.Path == key || b.Path.StartsWith(key + ".")))) binding.Update(); }
            finally { _updating = wasUpdating; }
            Changed?.Invoke(ns,key);
        });
    }

    public async Task Load(params string[] namespaces)
    {
        foreach (var ns in namespaces)
            _values[ns] = JsonNode.Parse((await _backend.StateAsync(ns, "settings")).GetRawText()) as JsonObject ?? new();
    }
    public async Task Reload()
    {
        await Load(_values.Keys.ToArray());
        bool wasUpdating = _updating; _updating = true;
        try { foreach (var binding in _bindings.ToArray()) binding.Update(); }
        finally { _updating = wasUpdating; }
        foreach (string ns in _values.Keys.ToArray()) Changed?.Invoke(ns, "");
    }
    public JsonObject Values(string ns) => _values[ns];
    public JsonNode? Value(string ns, string path)
    {
        JsonNode? node = _values.GetValueOrDefault(ns);
        foreach (var part in path.Split('.')) node = (node as JsonObject)?[part];
        return node;
    }
    public async Task Save(string ns, string path, JsonNode? value)
    {
        await _saving.WaitAsync();
        try {
        string[] parts = path.Split('.');
        var root = Values(ns).DeepClone().AsObject();
        var node = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject) node[parts[i]] = new JsonObject();
            node = node[parts[i]]!.AsObject();
        }
        node[parts[^1]] = value?.DeepClone();
        try
        {
            await _backend.CallAsync("setting-factory-main", "set", ns, parts[0], root[parts[0]]);
            Values(ns)[parts[0]] = root[parts[0]]?.DeepClone();
            Status.Text = Localization.Text("已保存", "Saved");
        }
        catch (Exception ex) { Status.Text = Localization.Text("保存失败：", "Save failed: ") + ex.Message; throw; }
        } finally { _saving.Release(); }
    }
    public static StackPanel Section(string title, params UIElement[] controls)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var control in controls) panel.Children.Add(control);
        panel.Loaded += (_, _) => NativeFormText.Apply(panel);
        return panel;
    }
    public static ScrollViewer Scroll(UIElement panel) => new() { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24) };
    public ToggleSwitch Toggle(string ns, string key, string label, bool fallback = false)
    {
        var control = new ToggleSwitch { Header = label, OnContent = "开", OffContent = "关", IsOn = Value(ns, key)?.GetValue<bool>() ?? fallback };
        Bind(control, ns, key, () => control.IsOn = Value(ns, key)?.GetValue<bool>() ?? fallback);
        control.Toggled += async (_, _) => { if (_updating) return; try { await Save(ns, key, JsonValue.Create(control.IsOn)); } catch { } };
        return control;
    }
    public NumberBox Number(string ns, string key, string label, double min = 0, double max = 99999, double fallback = 0, double step = 1)
    {
        var control = new NumberBox { Header = label, Value = Value(ns, key)?.GetValue<double>() ?? fallback, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        Bind(control, ns, key, () => control.Value = Value(ns, key)?.GetValue<double>() ?? fallback);
        control.ValueChanged += async (_, _) => { if (!_updating && !double.IsNaN(control.Value)) try { await Save(ns, key, JsonValue.Create(control.Value)); } catch { } };
        return control;
    }
    public TextBox Text(string ns, string key, string label, string fallback = "", bool multiline = false)
    {
        var control = new TextBox { Header = label, Text = Value(ns, key)?.GetValue<string>() ?? fallback, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch };
        Bind(control, ns, key, () => { if (control.FocusState == FocusState.Unfocused) control.Text = Value(ns, key)?.GetValue<string>() ?? fallback; });
        control.LostFocus += async (_, _) => { if (_updating) return; try { await Save(ns, key, JsonValue.Create(control.Text)); } catch { } };
        return control;
    }
    public ComboBox Choice(string ns, string key, string label, params (string Value, string Label)[] options)
    {
        var control = Select(label, options, Value(ns, key)?.GetValue<string>());
        Bind(control, ns, key, () =>
        {
            string? selected = Value(ns, key)?.GetValue<string>();
            var item = control.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is string value && value == selected);
            // An absent or obsolete stored enum must not clear a valid selection,
            // trigger dependent controls with null, or write a fallback to disk.
            if (item is not null && !ReferenceEquals(control.SelectedItem, item)) control.SelectedItem = item;
        });
        control.SelectionChanged += async (_, _) => { if (!_updating && TrySelected(control, out string selected)) try { await Save(ns, key, JsonValue.Create(selected)); } catch { } };
        return control;
    }
    public ComboBox NumericChoice(string ns, string key, string label, params int[] options)
    {
        var control = Select(label, options.Select(value => (value.ToString(), value.ToString())), Value(ns, key)?.ToString());
        Bind(control, ns, key, () => control.SelectedItem = control.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == Value(ns, key)?.ToString()) ?? control.SelectedItem);
        control.SelectionChanged += async (_, _) => { if (!_updating && TrySelected(control, out string selected) && int.TryParse(selected, out int value)) try { await Save(ns, key, JsonValue.Create(value)); } catch { } };
        return control;
    }
    public static ComboBox Select(string label, IEnumerable<(string Value, string Label)> options, string? selected = null)
    {
        var control = new ComboBox { Header = label, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (value, text) in options) { var item = new ComboBoxItem { Content = text, Tag = value }; control.Items.Add(item); if (value == selected) control.SelectedItem = item; }
        if (control.SelectedItem is null && control.Items.Count > 0) control.SelectedIndex = 0;
        control.Loaded += (_, _) => NativeFormText.Apply(control);
        return control;
    }
    public Button Action(string label, string ns, string method, params object?[] args)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => { button.IsEnabled = false; try { var result = await _backend.CallAsync(ns, method, args); Status.Text = result.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(result.GetString()) ? result.GetString() : Localization.Text("操作完成", "Done"); } catch (Exception ex) { Status.Text = ex.Message; } finally { button.IsEnabled = true; } };
        return button;
    }
    public static bool TrySelected(ComboBox control, out string value)
    {
        value = control.SelectedItem is ComboBoxItem { Tag: string selected } ? selected : "";
        return control.SelectedItem is ComboBoxItem { Tag: string };
    }
    public static string Selected(ComboBox control) => TrySelected(control, out string value) ? value : "";
}
