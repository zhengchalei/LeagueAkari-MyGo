using System.Globalization;

namespace LeagueAkari.WinUI.Services;

public static class HostNotificationRules
{
    public static bool ShowStreamingHint(bool tools, bool clientSetting, bool streamerMode, bool neverShow, double dismissedAt, long now) =>
        (tools || clientSetting) && !streamerMode && !neverShow && now - dismissedAt >= 3 * 24 * 60 * 60 * 1000L;

    public static string FormatSeconds(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var days = Math.Floor(seconds / 86400); var hours = Math.Floor(seconds % 86400 / 3600); var minutes = Math.Floor(seconds % 3600 / 60); var secs = seconds % 60;
        return days > 0 ? $"{days}day {hours:0}h" : hours > 0 ? $"{hours}h {minutes:0}m" : minutes > 0 ? $"{minutes}m {secs:0}s" : secs.ToString("0", CultureInfo.InvariantCulture) + "s";
    }
}

/// <summary>Low/medium announcements become read when opened; high requires the read action.</summary>
public sealed class AnnouncementReadState(string uniqueId, string alertLevel, string lastReadId)
{
    public bool IsRead { get; private set; } = uniqueId.Length > 0 && uniqueId == lastReadId;
    public bool Open() => alertLevel is "low" or "medium" && MarkRead();
    public bool MarkRead()
    {
        if (IsRead || uniqueId.Length == 0) return false;
        IsRead = true; return true;
    }
    public string PrimaryButtonKey => IsRead ? "announcements.modal.close" : "announcements.modal.read";
}

/// <summary>Same restart-on-mismatch and per-key timeout as useKeyboardCombo.</summary>
public sealed class NotificationKeyCombo(string sequence, int timeoutMilliseconds)
{
    private int _position;
    private long _lastKey;
    public bool Push(char key, long now)
    {
        if (_position > 0 && timeoutMilliseconds > 0 && now - _lastKey >= timeoutMilliseconds) _position = 0;
        key = char.ToUpperInvariant(key);
        if (key != sequence[_position]) _position = 0;
        if (key != sequence[_position]) return false;
        _lastKey = now;
        if (++_position < sequence.Length) return false;
        _position = 0; return true;
    }
}

public sealed class FunnyPricingCredit
{
    public double Balance { get; private set; }
    public string Current { get; private set; } = "basic";
    public void Open() => Balance = 0;
    public void Gift() => Balance += 1000000;
    public double TopUp()
    {
        var amount = Balance < 29000 ? 1000 : Balance < 29900 ? 50 : Balance < 29950 ? 1 : Balance < 30005 ? .5 : 10000;
        Balance += amount; return amount;
    }
    public bool Buy(string id, double price)
    {
        if (id == Current || price > Balance) return false;
        Balance -= price; Current = id; return true;
    }
}
