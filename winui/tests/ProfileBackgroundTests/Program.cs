using System.Text.Json;
using LeagueAkari.WinUI.Services;

static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
using var document = JsonDocument.Parse("""
{"id":99,"skins":[
 {"id":99000,"name":"Classic","uncenteredSplashPath":"/classic.jpg","chromas":[{"id":99001,"name":"Chroma"}]},
 {"id":99010,"name":"Quest","uncenteredSplashPath":"/quest.jpg","skinAugments":{"augments":[{"contentId":"base-overlay","overlays":[{"uncenteredLCOverlayPath":"/base.png"}]},{"contentId":"invalid-no-overlays"}]},"questSkinInfo":{"tiers":[
  {"id":99010,"name":"Duplicate tier"},
  {"id":99011,"name":"Tier 2","uncenteredSplashPath":"/tier2.jpg","skinAugments":{"augments":[{"contentId":"tier-overlay","overlays":[{"uncenteredLCOverlayPath":"/tier-overlay.png"},{"uncenteredLCOverlayPath":"/second.png"}]}]}}
 ]}},
 {"id":99011,"name":"Duplicate later skin"},
 {"id":99012,"name":"Other","uncenteredSplashPath":"/other.jpg"}
]}
""");
var skins = ProfileBackgroundData.Read(document.RootElement);
Check(skins.Select(s => s.Id).SequenceEqual(new[] { 99000, 99010, 99011, 99012 }), "Original quest tiers are included and IDs deduplicated in source order");
Check(skins.All(s => s.Id != 99001), "Original background choices do not include chromas");
Check(skins[1].Augments.Length == 1 && skins[1].Augments[0].ContentId == "base-overlay", "Only augment options with overlay lists appear");
Check(skins[2].Name == "Tier 2" && skins[2].SplashPath == "/tier2.jpg" && skins[2].Augments[0].OverlayPaths.SequenceEqual(new[] { "/tier-overlay.png", "/second.png" }), "Quest tier previews retain the complete overlay order");
var unchanged = ProfileBackgroundData.Writes(skins[1], null);
Check(unchanged.Length == 1 && unchanged[0].Key == "backgroundSkinId" && (int)unchanged[0].Value == 99010, "Undefined augment sends only skin and keeps client augment state");
var unset = ProfileBackgroundData.Writes(skins[1], "");
Check(unset.Length == 2 && unset[1].Key == "backgroundSkinAugments" && (string)unset[1].Value == "", "Selecting unset explicitly writes an empty augment string");
Check((string)ProfileBackgroundData.Writes(skins[2], "tier-overlay")[1].Value == "tier-overlay", "Selected quest augment uses its actual content ID");
try { ProfileBackgroundData.Writes(skins[2], "base-overlay"); throw new Exception("Other skin's augment was allowed"); } catch (InvalidOperationException) { }
Check(ProfileBackgroundData.Read(JsonDocument.Parse("{}").RootElement).Length == 0, "Missing champion data does not synthesize skin choices");
Console.WriteLine("9 original profile background behavior fixtures passed.");
