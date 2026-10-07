using System.Text.Json;
using System.Text.Json.Nodes;

internal static class FixtureChampionConfig
{
    public static object Enhance(object original)
    {
        if (Environment.GetEnvironmentVariable("WINUI_FIXTURE_CHAMPION_CONFIG") != "1") return original;
        var data = JsonSerializer.SerializeToNode(original)!.AsObject();
        foreach (var (id, champion) in data["champions"]!.AsObject()) champion!["id"] = int.Parse(id);
        foreach (var (id, spell) in data["summonerSpells"]!.AsObject()) { spell!["id"] = int.Parse(id); spell["gameModes"] = JsonSerializer.SerializeToNode(new[] { "CLASSIC", "ARAM", "URF", "NEXUSBLITZ", "ULTBOOK" }); }
        object Style(int id, int other, int offset) => new { id, name = id == 8000 ? "精密" : "主宰", iconPath = $"/test/styles/{id}.png", allowedSubStyles = new[] { other }, slots = new[] {
            new { type="kKeyStone",perks=new[]{offset+1,offset+2}},new {type="kMixedRegularSplashable",perks=new[]{offset+3,offset+4}},new {type="kMixedRegularSplashable",perks=new[]{offset+5,offset+6}},new {type="kMixedRegularSplashable",perks=new[]{offset+7,offset+8}},
            new {type="kStatMod",perks=new[]{5005,5008}},new {type="kStatMod",perks=new[]{5008,5002}},new {type="kStatMod",perks=new[]{5002,5001}} } };
        data["perkstyles"] = JsonSerializer.SerializeToNode(new { schemaVersion = 2, styles = new Dictionary<string, object> { ["8000"] = Style(8000, 8100, 100), ["8100"] = Style(8100, 8000, 200) } });
        data["perks"] = JsonSerializer.SerializeToNode(Enumerable.Range(101, 8).Concat(Enumerable.Range(201, 8)).Concat(new[] { 5005, 5008, 5002, 5001 }).ToDictionary(id => id.ToString(), id => (object)new { id, name = "测试符文" + id, iconPath = $"/test/perks/{id}.png", longDesc = "模拟符文说明" }));
        return data;
    }
}
