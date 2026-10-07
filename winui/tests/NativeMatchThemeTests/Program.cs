using LeagueAkari.WinUI.Services;

int count = 0;
void Check(string title, bool ok) { if (!ok) throw new Exception(title); ++count; Console.WriteLine("PASS " + title); }
var lightWin = NativeMatchColors.History("win", false, "light");
var lightLoss = NativeMatchColors.History("loss", false, "light");
var darkWin = NativeMatchColors.History("win", true, "dark");
var darkLoss = NativeMatchColors.History("loss", true, "dark");
Check("Light win exact source shadow composite", lightWin.Background == new NativeRgb(193,217,247));
Check("Light loss exact source shadow composite", lightLoss.Background == new NativeRgb(243,192,192));
Check("Dark win exact source shadow composite", darkWin.Background == new NativeRgb(25,35,57));
Check("Dark loss exact source shadow composite", darkLoss.Background == new NativeRgb(50,22,29));
Check("Dark semantic result labels use original readable blues and reds", darkWin.Result == new NativeRgb(147,197,253) && darkLoss.Result == new NativeRgb(252,165,165));
Check("Light semantic result labels use original blues and reds", lightWin.Result == new NativeRgb(37,99,235) && lightLoss.Result == new NativeRgb(185,28,28));
Check("System-default dark uses dark result shadow", NativeMatchColors.History("win", true, "default") == darkWin);
Check("System-default light uses light result shadow", NativeMatchColors.History("loss", false, "default") == lightLoss);
Check("Remake and abort retain neutral rather than red", NativeMatchColors.History("remake", true, "dark") == NativeMatchColors.History("abort", true, "dark") && NativeMatchColors.History("remake", true, "dark") != darkLoss);
Check("Theme switches reverse the same outcome palette", NativeMatchColors.History("win", true, "light").Background != lightWin.Background);
foreach (var theme in new[] { "butter", "graphite", "cyber", "sakura", "mint", "aurora" })
{
    var dark = theme is "graphite" or "cyber" or "aurora";
    var win = NativeMatchColors.History("win", dark, theme); var loss = NativeMatchColors.History("loss", dark, theme); var neutral = NativeMatchColors.History("remake", dark, theme);
    Check(theme + " preserves three distinct source result tints", win.Background != loss.Background && neutral.Background != win.Background && neutral.Background != loss.Background);
}
Check("Original graphite differs from plain-dark result fill", NativeMatchColors.History("win", true, "graphite").Background != darkWin.Background);
var ongoingWin = NativeMatchColors.Ongoing("win", true); var ongoingLoss = NativeMatchColors.Ongoing("loss", true);
Check("Ongoing dark original blue/red tints remain distinct", ongoingWin.Background == new NativeRgb(30,48,78) && ongoingLoss.Background == new NativeRgb(76,33,34));
Check("Ongoing light original tints remain blue/red", NativeMatchColors.Ongoing("win", false).Background.B > NativeMatchColors.Ongoing("win", false).Background.R && NativeMatchColors.Ongoing("loss", false).Background.R > NativeMatchColors.Ongoing("loss", false).Background.B);
Check("Ongoing neutral does not look like defeat", NativeMatchColors.Ongoing("abort", true).Background == NativeMatchColors.Ongoing("remake", true).Background && NativeMatchColors.Ongoing("abort", true).Background != ongoingLoss.Background);
Console.WriteLine($"Native match theme fixtures: {count} passed");
