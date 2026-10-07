using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record FixedTextPreset(string Id, string Title, string Content, string? Shortcut)
{
    public static FixedTextPreset Read(JsonElement item) => new(item.Text("id"), item.Text("title"), item.Text("content"), item.Text("shortcut") is { Length: > 0 } shortcut ? shortcut : null);
}
public sealed class FixedTextPresetController(Func<string, string, object?[], Task<JsonElement>> call)
{
    public const int MaxItems = 100, TitleLimit = 64, ContentLimit = 65536;
    private bool _active; private int _generation, _refresh;
    public FixedTextPreset[] Items { get; private set; } = [];
    public string? SelectedId { get; private set; }
    public FixedTextPreset? Selected => Items.FirstOrDefault(item => item.Id == SelectedId);
    public string Title { get; private set; } = "";
    public string Content { get; private set; } = "";
    public bool Dirty => Selected is { } item && (Title != item.Title || Content != item.Content);
    public bool Busy { get; private set; }
    public Exception? Error { get; private set; }
    public bool CanCreate => !Busy && Items.Length < MaxItems;
    public event Action? Changed;
    public void Activate() => _active = true;
    public void Deactivate() { _active = false; ++_generation; Busy = false; }
    public void Edit(string title, string content) { Title = title[..Math.Min(title.Length, TitleLimit)]; Content = content[..Math.Min(content.Length, ContentLimit)]; Changed?.Invoke(); }
    public bool CanMove(string direction) { int index = Array.FindIndex(Items, item => item.Id == SelectedId); return !Busy && index >= 0 && direction is "up" or "down" && (direction == "up" ? index > 0 : index < Items.Length - 1); }
    public void ApplyItems(JsonElement values)
    {
        ++_refresh;
        bool preserveDraft = Dirty; string? previous = SelectedId;
        Items = values.Items().Select(FixedTextPreset.Read).Where(item => item.Id.Length > 0).DistinctBy(item => item.Id).Take(MaxItems).ToArray();
        if (Selected is null) SelectedId = Items.FirstOrDefault()?.Id;
        if (!preserveDraft || previous != SelectedId) RestoreDraft();
        Changed?.Invoke();
    }
    private void RestoreDraft() { Title = Selected?.Title ?? ""; Content = Selected?.Content ?? ""; }
    public async Task RefreshAsync()
    {
        int generation = _generation, refresh = ++_refresh;
        try { var settings = await call("winui-backend", "snapshot", ["in-game-send-main", "settings"]); if (_active && generation == _generation && refresh == _refresh) { Error = null; ApplyItems(settings.Field("fixedTextPresetItems")); } }
        catch (Exception error) { if (_active && generation == _generation && refresh == _refresh) { Error = error; Changed?.Invoke(); } }
    }
    private async Task<bool> RunAsync(Func<int, Task<bool>> operation)
    {
        if (!_active || Busy) return false;
        int generation = _generation; ++_refresh; Busy = true; Error = null; Changed?.Invoke();
        try { return await operation(generation); }
        catch (Exception error) { if (_active && generation == _generation) Error = error; return false; }
        finally { if (_active && generation == _generation) { Busy = false; Changed?.Invoke(); } }
    }
    private bool Current(int generation) => _active && generation == _generation;
    private void Put(FixedTextPreset item) => Items = Items.Select(existing => existing.Id == item.Id ? item : existing).ToArray();
    private async Task<bool> SaveCoreAsync(int generation)
    {
        if (!Dirty || Selected is null) return true;
        var item = Selected; string title = Title, content = Content;
        var result = await call("in-game-send-main", "updateFixedTextPresetItem", [item.Id, new { title, content }]);
        if (!Current(generation)) return false;
        Put(result.ValueKind == JsonValueKind.Object ? FixedTextPreset.Read(result) : item with { Title = title, Content = content });
        Changed?.Invoke(); return true;
    }
    public Task<bool> SaveAsync() => RunAsync(SaveCoreAsync);
    public Task<bool> SelectAsync(string id) => RunAsync(async generation =>
    {
        if (id == SelectedId) return true;
        if (!await SaveCoreAsync(generation) || !Current(generation) || !Items.Any(item => item.Id == id)) return false;
        SelectedId = id; RestoreDraft(); return true;
    });
    public Task<bool> CreateAsync() => RunAsync(async generation =>
    {
        if (Items.Length >= MaxItems || !await SaveCoreAsync(generation)) return false;
        var result = await call("in-game-send-main", "createFixedTextPresetItem", []);
        if (!Current(generation)) return false;
        var item = FixedTextPreset.Read(result); if (item.Id.Length == 0) throw new InvalidOperationException("Invalid fixed text preset response");
        Items = Items.Where(existing => existing.Id != item.Id).Append(item).ToArray(); SelectedId = item.Id; RestoreDraft(); return true;
    });
    public Task<bool> MoveAsync(string direction)
    {
        if (!CanMove(direction)) return Task.FromResult(false);
        return RunAsync(async generation =>
        {
            string id = SelectedId!; if (!await SaveCoreAsync(generation)) return false;
            int originalIndex = Array.FindIndex(Items, item => item.Id == id);
            var result = await call("in-game-send-main", "moveFixedTextPresetItem", [id, direction]);
            if (!Current(generation) || result.ValueKind == JsonValueKind.False) return false;
            int index = Array.FindIndex(Items, item => item.Id == id), target = direction == "up" ? index - 1 : index + 1;
            if (index == originalIndex && index >= 0 && target >= 0 && target < Items.Length) (Items[index], Items[target]) = (Items[target], Items[index]);
            return true;
        });
    }
    public Task<bool> DeleteAsync(Func<FixedTextPreset, Task<bool>> confirm) => RunAsync(async generation =>
    {
        if (Selected is not { } item || !await confirm(item) || !Current(generation) || SelectedId != item.Id) return false;
        int index = Array.FindIndex(Items, existing => existing.Id == item.Id);
        var result = await call("in-game-send-main", "deleteFixedTextPresetItem", [item.Id]);
        if (!Current(generation) || result.ValueKind == JsonValueKind.False) return false;
        Items = Items.Where(existing => existing.Id != item.Id).ToArray();
        SelectedId = Items.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, Items.Length - 1)))?.Id; RestoreDraft(); return true;
    });
    public Task<bool> SetShortcutAsync(string? shortcut) => RunAsync(async generation =>
    {
        if (Selected is not { } item) return false;
        var result = await call("in-game-send-main", "updateFixedTextPresetItem", [item.Id, new { shortcut }]);
        if (!Current(generation)) return false;
        Put(result.ValueKind == JsonValueKind.Object ? FixedTextPreset.Read(result) : item with { Shortcut = shortcut }); return true;
    });
    public static string? SendDisabledReason(string phase, bool nativeAvailable, bool hasItem, bool dirty, bool busy) => !hasItem ? "noSelection" : dirty ? "saveFirst" : busy ? "busy"
        : phase == "draft" ? "draftOnly" : phase == "in-game" && !nativeAvailable ? "nativeInput" : phase is not ("in-game" or "champ-select" or "lobby") ? "unavailable" : null;
    public Task<bool> SendAsync(Func<Task<(string Phase, bool NativeAvailable)>> current) => RunAsync(async generation =>
    {
        var status = await current(); if (!Current(generation) || SendDisabledReason(status.Phase, status.NativeAvailable, Selected is not null, Dirty, false) is not null) return false;
        var result = await call("in-game-send-main", "sendFixedTextPreset", [SelectedId]);
        if (result.ValueKind != JsonValueKind.True) throw new InvalidOperationException("send-unavailable"); return Current(generation);
    });
}
