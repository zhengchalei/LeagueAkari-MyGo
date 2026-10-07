using System.Reflection;
using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

/// <summary>The original renderer dictionaries are shared by the native views.</summary>
public static class Localization
{
    private static readonly Dictionary<string, string> Chinese = Load("zh-CN");
    private static readonly Dictionary<string, string> English = Load("en");
    private static readonly Dictionary<string, string> LiteralEnglish = Chinese
        .Where(p => English.ContainsKey(p.Key))
        .GroupBy(p => p.Value).ToDictionary(g => g.Key, g => English[g.First().Key]);
    private static readonly Dictionary<string, string> NativeEnglish = new()
    {
        ["我的战绩"] = "My history",
        ["连接"] = "Connect",
        ["选择 LOL 客户端"] = "Select a League client",
        ["迷你窗口"] = "Mini window",
        ["所有排位"] = "All ranked games",
        ["所有普通对局"] = "All normal games",
        ["当前客户端大区"] = "Current client server",
        ["搜索历史 / 好友"] = "Search history / friends",
        ["筛选条件"] = "Filters",
        ["停止收集"] = "Stop collecting",
        ["收集符合条件的战绩"] = "Collect matching games",
        ["复制名字"] = "Copy name",
        ["每次查询"] = "Games per request",
        ["目标数量"] = "Target count",
        ["最大次数"] = "Maximum requests",
        ["复制 Riot ID"] = "Copy Riot ID",
        ["玩家资料"] = "Player profile",
        ["队列"] = "Queue",
        ["分页"] = "Pagination",
        ["全部位置"] = "All positions",
        ["全部英雄"] = "All champions",
        ["收集战绩"] = "Collect games",
        ["战绩"] = "History",
        ["刷新战绩"] = "Refresh history",
        ["更多功能"] = "More tools",
        ["原生工具集"] = "Native toolkit",
        ["搜索玩家"] = "Search players",
        ["国服大区"] = "Chinese server",
        ["大区"] = "Server",
        ["搜索名字 / Riot ID"] = "Search name / Riot ID",
        ["当前玩家"] = "Current player",
        ["刷新全部"] = "Refresh all",
        ["整队刷新"] = "Refresh team",
        ["结束模拟"] = "End simulation",
        ["详细指标"] = "Detailed stats",
        ["当前对局模式"] = "Current game mode",
        ["所有模式"] = "All modes",
        ["已停止收集"] = "Collection stopped",
        ["暂无符合条件的战绩"] = "No matching games",
        ["开始收集"] = "Start collecting",
        ["应用筛选"] = "Apply filters",
        ["清空筛选"] = "Clear filters",
        ["仅显示胜利"] = "Victories only",
        ["按英雄收集"] = "Collect by champion",
        ["按位置收集"] = "Collect by position",
        ["皮肤快捷选择"] = "Quick skin selection",
        ["原生窗口"] = "Native window",
        ["设置"] = "Settings",
        ["自动操作"] = "Automation",
        ["工具集"] = "Toolkit",
        ["对局"] = "Ongoing game",
        ["保存"] = "Save",
        ["取消"] = "Cancel",
        ["关闭"] = "Close",
        ["应用"] = "Apply",
        ["搜索"] = "Search",
        ["复制"] = "Copy",
        ["重置"] = "Reset",
        ["下一页"] = "Next page",
        ["上一页"] = "Previous page",
        ["获取更多"] = "Load more"
    };
    public static string Locale { get; private set; } = "zh-CN";
    public static bool IsEnglish => Locale == "en";
    public static event Action? Changed;

    public static void SetLocale(string? locale)
    {
        var next = locale?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "zh-CN";
        if (next == Locale) return;
        Locale = next;
        Changed?.Invoke();
    }

    public static string Text(string chinese, string english) => IsEnglish ? english : chinese;
    public static string Translate(string chinese) => IsEnglish ? NativeEnglish.GetValueOrDefault(chinese) ?? LiteralEnglish.GetValueOrDefault(chinese) ?? chinese : chinese;
    public static string Key(string key, string? fallback = null, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var dictionary = IsEnglish ? English : Chinese;
        var plural = arguments != null && arguments.TryGetValue("count", out var count) && double.TryParse(Convert.ToString(count, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number) && number == 1 ? "_one" : "_other";
        var text = dictionary.GetValueOrDefault(key + plural) ?? dictionary.GetValueOrDefault(key) ?? fallback ?? key;
        if (arguments != null)
            foreach (var argument in arguments) text = text.Replace("{{" + argument.Key + "}}", Convert.ToString(argument.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\$t\(([^)]+)\)", match =>
        {
            var nested = match.Groups[1].Value.Replace(':', '.');
            return dictionary.GetValueOrDefault(nested) ?? nested;
        });
        return text;
    }

    private static Dictionary<string, string> Load(string locale)
    {
        var assembly = typeof(Localization).Assembly;
        using var resource = assembly.GetManifestResourceStream($"LeagueAkari.WinUI.Services.Locales.{locale}.json");
        return resource == null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(resource) ?? [];
    }
}
