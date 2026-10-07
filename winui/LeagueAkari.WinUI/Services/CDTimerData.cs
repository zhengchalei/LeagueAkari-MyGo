using System.Text.Json;
namespace LeagueAkari.WinUI.Services;

public static class CDTimerData
{
    public static double? AbilityHaste(JsonElement platform, string mode)
    {
        var config = platform.Field("supportedGameModes").Items().FirstOrDefault(item => item.Text("gameMode") == mode);
        return MatchDetailsData.Number(config.Field("abilityHaste"));
    }
    public static long? CountdownTarget(long now, double? cooldown, double? haste) => cooldown is > 0 && haste is >= 0 ? now + (long)(cooldown.Value * 100 / (100 + haste.Value) * 1000) : null;
    public static long Adjust(string type, long timestamp, int wheelDelta, bool reverse, long now)
    {
        // Win32 wheel direction is the inverse of the original DOM deltaY.
        var next = timestamp + wheelDelta * -50L * (reverse ? -1 : 1);
        return type == "countup" ? Math.Min(next, now) : Math.Max(next, now);
    }
    public static long AdjustmentIndicatorDelta(string type, long previous, long adjusted, int wheelDelta, bool reverse) =>
        type == "countup" ? adjusted - previous : wheelDelta * -50L * (reverse ? -1 : 1);

    public static string SendFailureKey(string reason) => reason.Contains("AlreadySending", StringComparison.Ordinal) ? "cdTimer.window.alreadySending" :
        reason.Contains("GameClientNotForeground", StringComparison.Ordinal) || reason.Contains("not foreground", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("LOL 游戏未处于前台", StringComparison.Ordinal) || reason.Contains("LOL 游戏已离开前台", StringComparison.Ordinal) ? "cdTimer.window.gameNotForeground" : "cdTimer.window.sendFailed";
    public static string Display(string type, long timestamp, long now)
    {
        var seconds = (type == "countdown" ? timestamp - now : now - timestamp) / 1000d;
        return seconds < 0 ? "OK" : seconds > 999 ? "999" : seconds < 10 ? seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Floor(seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
