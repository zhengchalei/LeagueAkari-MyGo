using System.Text.Json;
using LeagueAkari.WinUI.Services;

int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
Check(ApplicationSettingsData.BackgroundMode("mica", true) == "mica", "Existing mixed preferences retain original Mica precedence");
Check(ApplicationSettingsData.BackgroundMode("none", true) == "profile-skin", "Profile skin restored when material is none");
Check(ApplicationSettingsData.BackgroundMode("none", false) == "none", "Plain background restored");
foreach (string mode in new[] { "profile-skin", "mica", "none" })
{
    var writes = ApplicationSettingsData.BackgroundWrites(mode);
    bool profile = mode != "profile-skin"; string material = mode == "profile-skin" ? "mica" : "none";
    foreach (var write in writes)
    {
        if (write.Namespace == "window-manager-main" && write.Key == "backgroundMaterial") material = (string)write.Value;
        else if (write.Namespace == "main-window-ui-renderer" && write.Key == "useProfileSkinAsBackground") profile = (bool)write.Value;
        else throw new Exception("Wrong persistence contract");
        Check(!(material == "mica" && profile), mode + ": each successful write leaves mutually exclusive backgrounds");
    }
    Check(ApplicationSettingsData.BackgroundMode(material, profile) == mode, mode + ": final persisted preference restores selected mode");
}
var remote = J("""{"latestRelease":{"version":"0.6.0","isNew":true}}""");
var idle = ApplicationSettingsData.Update(remote, J("""{"updateProgressInfo":null}"""), false, false);
Check(idle.HasRelease && idle.IsNewRelease && idle.CanDownload && !idle.IsUpdating && !idle.CanOpenDirectory, "New release can download only before an update exists");
var current = ApplicationSettingsData.Update(J("""{"latestRelease":{"isNew":false}}"""), J("{}"), false, false);
Check(current.HasRelease && !current.IsNewRelease && !current.CanDownload, "Current release exposes notes but no download");
var missing = ApplicationSettingsData.Update(J("""{"latestRelease":null}"""), J("{}"), false, false);
Check(!missing.HasRelease && !missing.CanDownload, "No release hides notes and download");
var downloading = ApplicationSettingsData.Update(remote, J("""{"updateProgressInfo":{"phase":"downloading","downloadingProgress":0.82,"downloadTimeLeft":18}}"""), false, false);
Check(!downloading.CanDownload && downloading.IsUpdating && downloading.CanOpenDirectory && downloading.Progress == .82 && downloading.SecondsLeft == 18, "Live download has progress, cancellation and directory access without duplicate start");
var failed = ApplicationSettingsData.Update(remote, J("""{"updateProgressInfo":{"phase":"download-failed"}}"""), false, false);
Check(!failed.CanDownload && failed.IsUpdating && !failed.CanOpenDirectory, "Failed update remains cancelable until cleared; no misleading prepared directory");
var ready = ApplicationSettingsData.Update(remote, J("""{"updateProgressInfo":{"phase":"waiting-for-restart"}}"""), false, false);
Check(ready.CanOpenDirectory && !ready.CanDownload && ready.IsUpdating, "Prepared update waits for exit and retains directory access");
Check(!ApplicationSettingsData.Update(remote, J("{}"), true, false).CanCheck, "Remote check busy prevents duplicate checks");
Check(!ApplicationSettingsData.Update(remote, J("{}"), false, true).CanDownload, "Pending start request prevents a second download before progress arrives");
foreach (string outcome in new[] { "failed", "new-updates", "no-updates" }) Check(ApplicationSettingsData.CheckResultKey(J("{\"result\":\"" + outcome + "\"}")) == "settings.app.selfUpdate.checkUpdatesResult." + outcome, "Check feedback distinguishes " + outcome);
Check(ApplicationSettingsData.FasterSource(24.5, -1), "Responsive source preferred over timed out source");
Check(!ApplicationSettingsData.FasterSource(-1, 24.5), "Timed out source cannot be displayed as faster");
Check(!ApplicationSettingsData.FasterSource(-1, -1) && !ApplicationSettingsData.FasterSource(10, 10), "No faster badge for both failed or equal latency");
Console.WriteLine($"Application settings contracts: {passed} passed");
