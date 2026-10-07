using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record SavedTag(string Puuid, string SelfPuuid, string Region, string Platform, string Tag)
{
    public string Server => SavedTagData.Server(Region, Platform);
    public object Update(string? text) => new { puuid = Puuid, selfPuuid = SelfPuuid, tag = string.IsNullOrEmpty(text) ? null : text };
}

public static class SavedTagData
{
    public static SavedTag[] Read(JsonElement response) => response.Field("data").Items()
        .Select(row => new SavedTag(row.Text("puuid"), row.Text("selfPuuid"), row.Text("region"), row.Text("rsoPlatformId"), row.Text("tag"))).ToArray();
    public static string Server(string region, string platform)
    {
        string value = region.ToUpperInvariant();
        return value == "TENCENT" ? "TENCENT_" + platform.ToUpperInvariant() : value switch
        {
            "NA" => "NA1", "BR" => "BR1", "TR" => "TR1", "LAN" => "LA1", "LAS" => "LA2", "OCE" => "OC1", "EUW1" => "EUW", "JP1" => "JP", _ => value
        };
    }
    public static int LastPage(int total, int size) => Math.Max(1, (total + size - 1) / size);
}
