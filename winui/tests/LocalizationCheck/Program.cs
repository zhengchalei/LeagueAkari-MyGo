using LeagueAkari.WinUI.Services;
static void Check(bool result, string message) { if (!result) throw new Exception(message); }
var changes = 0;
Localization.Changed += () => changes++;
Check(Localization.Key("matchCard.statKeys.kills") == "击杀", "Original Chinese stat dictionary");
Localization.SetLocale("en-US");
Check(Localization.IsEnglish && changes == 1, "Locale event");
Check(Localization.Key("matchCard.statKeys.kills") == "Kills", "Original English stat dictionary");
Check(Localization.Text("已有皮肤", "Owned skins") == "Owned skins", "Native paired text");
Check(Localization.Translate("胜利") is "Victory" or "Win", "Original renderer literal translation");
var sent = Localization.Key("cdTimer.window.countdown", null, new Dictionary<string, object?> { ["championName"] = "Ahri", ["spellName"] = "Flash", ["minutes"] = 3, ["seconds"] = "09" });
Check(sent == "Ahri Flash 3m09s until ready", "Native timer preserves placeholders and padding");
Check(Localization.Key("common.summonerPlaceholder", null, new Dictionary<string, object?> { ["index"] = 2 }) == "Summoner 2", "Streamer placeholder");
Localization.SetLocale("en");
Check(changes == 1, "No repeated locale event");
Localization.SetLocale("zh-CN");
Check(changes == 2 && Localization.Key("matchCard.statKeys.kills") == "击杀", "Switch back Chinese");
Console.WriteLine("8 native localization contracts passed.");
