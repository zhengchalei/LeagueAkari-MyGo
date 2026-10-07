using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record BackgroundSettingWrite(string Namespace, string Key, object Value);

public static class ApplicationSettingsData
{
    public static bool FasterSource(double latency, double other) => latency >= 0 && (other < 0 || latency < other);
    public static string BackgroundMode(string material, bool profileSkin) => material == "mica" ? "mica" : profileSkin ? "profile-skin" : "none";

    // Disable the previous rendering source before enabling its replacement.
    // A failed second write can leave a plain background, never both sources on.
    public static BackgroundSettingWrite[] BackgroundWrites(string mode) => mode switch
    {
        "profile-skin" => [new("window-manager-main", "backgroundMaterial", "none"), new("main-window-ui-renderer", "useProfileSkinAsBackground", true)],
        "mica" => [new("main-window-ui-renderer", "useProfileSkinAsBackground", false), new("window-manager-main", "backgroundMaterial", "mica")],
        "none" => [new("main-window-ui-renderer", "useProfileSkinAsBackground", false), new("window-manager-main", "backgroundMaterial", "none")],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static ApplicationUpdateState Update(JsonElement remote, JsonElement updater, bool checking, bool busy)
    {
        var release = remote.Field("latestRelease");
        var progress = updater.Field("updateProgressInfo");
        bool hasRelease = release.ValueKind == JsonValueKind.Object, updating = progress.ValueKind == JsonValueKind.Object;
        return new(hasRelease, hasRelease && release.Boolean("isNew"), !checking && !busy,
            hasRelease && release.Boolean("isNew") && !updating && !busy,
            updating, progress.Text("phase"), Math.Clamp(progress.Number("downloadingProgress"), 0, 1),
            Math.Max(0, progress.Number("downloadTimeLeft")), updater.Number("lastCheckAt"));
    }

    public static string CheckResultKey(JsonElement result) => result.Text("result") switch
    {
        "no-updates" => "settings.app.selfUpdate.checkUpdatesResult.no-updates",
        "new-updates" => "settings.app.selfUpdate.checkUpdatesResult.new-updates",
        "failed" => "settings.app.selfUpdate.checkUpdatesResult.failed",
        _ => throw new InvalidOperationException("Unknown update check result")
    };
}

public sealed record ApplicationUpdateState(bool HasRelease, bool IsNewRelease, bool CanCheck, bool CanDownload,
    bool IsUpdating, string Phase, double Progress, double SecondsLeft, double LastCheckAt)
{
    public bool CanOpenDirectory => IsUpdating && Phase is "downloading" or "waiting-for-restart";
}
