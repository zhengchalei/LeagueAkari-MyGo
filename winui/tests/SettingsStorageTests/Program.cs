using System.Text.Json;
using LeagueAkari.WinUI.Services;

void Check(bool value, string name) { if (!value) throw new Exception(name); }
var response = JsonSerializer.SerializeToElement(new { data = new[] { new { puuid = "other", selfPuuid = "self", region = "TENCENT", rsoPlatformId = "nj100", tag = "保留遇见记录" } } });
var row = SavedTagData.Read(response).Single();
Check(row.Server == "TENCENT_NJ100", "Original Tencent server normalization");
Check(SavedTagData.Server("na", "") == "NA1", "Original NA alias");
Check(SavedTagData.Server("OCE", "") == "OC1", "Original OCE alias");
Check(SavedTagData.Server("EUW1", "") == "EUW", "Original EUW alias");
var clear = JsonSerializer.SerializeToElement(row.Update(null));
Check(clear.Field("tag").ValueKind == JsonValueKind.Null, "Delete only clears tag");
Check(clear.Text("puuid") == "other" && clear.Text("selfPuuid") == "self" && clear.EnumerateObject().Count() == 3, "Original update DTO retains identity only");
Check(JsonSerializer.SerializeToElement(row.Update("")).Field("tag").ValueKind == JsonValueKind.Null, "Empty editor text clears tag");
Check(JsonSerializer.SerializeToElement(row.Update(" ")).Text("tag") == " ", "Do not invent whitespace trim");
Check(SavedTagData.LastPage(0, 20) == 1 && SavedTagData.LastPage(21, 20) == 2, "Bounded last page");
Check(DebugEndpoints.All.Length == 1008, "Original endpoint inventory");
var suggestions = DebugEndpoints.Suggest("lcs/v1/s").ToArray();
Check(suggestions.Contains("/lol-champ-select/v1/session"), "Original subsequence autocomplete");
Check(suggestions.Select(path => path.Length).SequenceEqual(suggestions.Select(path => path.Length).Order()), "Original shortest path first");
Check(!DebugEndpoints.IsSubsequence("abc", "acb"), "Subsequence must preserve order");
Console.WriteLine("13 original storage and debug behavior fixtures passed.");
