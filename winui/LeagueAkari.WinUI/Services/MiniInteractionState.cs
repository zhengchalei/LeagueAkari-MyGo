using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

/// <summary>Keeps request feedback local to the champion and phase that started it.</summary>
public sealed class MiniInteractionState
{
    private string _scope = "";
    private long _revision;
    private string _snapshotError = "", _dismissedSnapshotError = "";
    public string Error { get; private set; } = "";
    public string PendingKind { get; private set; } = "";
    public int PendingId { get; private set; }
    public bool SkinFailed { get; private set; }

    public void Observe(JsonElement snapshot)
    {
        _snapshotError = snapshot.Text("Error");
        string scope = snapshot.Boolean("Connected") + "/" + snapshot.Text("Phase") + "/" + snapshot.Number("ChampionID");
        if (_scope == scope) return;
        _scope = scope; _revision++; PendingKind = Error = _dismissedSnapshotError = ""; PendingId = 0; SkinFailed = false;
    }
    public long Begin(string kind, int id)
    {
        PendingKind = kind; PendingId = id; Error = "";
        _dismissedSnapshotError = _snapshotError;
        if (kind == "skin") SkinFailed = false;
        return ++_revision;
    }
    public void Complete(long revision, string error)
    {
        if (revision != _revision) return;
        SkinFailed = PendingKind == "skin" && error.Length > 0;
        Error = error; PendingKind = ""; PendingId = 0;
    }
    public string DisplayError(JsonElement snapshot) => Error.Length > 0 ? Error : snapshot.Text("Error") == _dismissedSnapshotError ? "" : snapshot.Text("Error");
    public string SkinStatus(JsonElement snapshot, bool english)
    {
        var skins = MiniSkinData.Owned(snapshot);
        if (PendingKind == "skin") return (english ? "Applying: " : "正在切换：") + skins.FirstOrDefault(s => s.Id == PendingId)?.Name;
        if (snapshot.Number("PendingSkinID") > 0) return (english ? "Applying: " : "正在切换：") + snapshot.Text("PendingSkinName");
        if (SkinFailed || snapshot.Boolean("SkinApplyFailed")) return english ? "Skin switch failed. Please try again." : "皮肤切换失败，请重试";
        if (snapshot.Boolean("SkinsLoading")) return english ? "Loading owned skins…" : "正在读取已有皮肤…";
        if (skins.Length == 0) return snapshot.Boolean("SkinsLoadFailed") || snapshot.Text("Error").Length > 0 ? (english ? "Unable to load owned skins" : "已有皮肤加载失败") : (english ? "No available owned skins" : "暂无可用的已有皮肤");
        if (!snapshot.Boolean("CanSelectSkin")) return english ? "Skin selection is currently unavailable" : "当前不能切换皮肤";
        var selected = skins.FirstOrDefault(s => s.Selected);
        return selected is null ? (english ? "Waiting for client selection" : "等待客户端确认选择") : (english ? "Selected: " : "已选择：") + selected.Name;
    }
}

public static class MiniViewData
{
    private static string Raw(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText();
    public static string SelectionSignature(JsonElement snapshot, JsonElement envelope, bool english, string theme)
    {
        var state = envelope.Field("state");
        var session = state.Field("champSelect").Field("session");
        var flow = state.Field("gameflow").Field("session");
        // Countdown ticks do not recreate the virtualized owned-skin grid.
        return Raw(snapshot) + "/" + english + "/" + theme + "/" + session.Boolean("isSpectating") + "/" + Raw(flow.Field("gameData").Field("queue")) + "/" + Raw(flow.Field("map")) + "/" + ShowReroll(snapshot, envelope);
    }
    public static bool ShowReroll(JsonElement snapshot, JsonElement envelope)
    {
        if (snapshot.Field("ShowReroll").ValueKind is JsonValueKind.True or JsonValueKind.False) return snapshot.Boolean("ShowReroll");
        var session = envelope.Field("state").Field("champSelect").Field("session");
        return snapshot.Number("Rerolls") > 0 || session.Boolean("allowRerolling") && session.Field("timer").Text("phase") == "FINALIZATION" && !session.Boolean("allowSubsetChampionPicks");
    }
}
