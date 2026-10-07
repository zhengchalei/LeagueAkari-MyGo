using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record ConnectionClient(int Pid, string Region, string Platform, JsonElement Command);
public static class ConnectionData
{
    public static ConnectionClient[] OtherClients(JsonElement state, JsonElement ux)
    {
        int connected = (int)state.Field("auth").Number("pid");
        return ux.Field("launchedClients").Items().Select(client => new ConnectionClient((int)client.Number("pid"), client.Text("region"), client.Text("rsoPlatformId"), client.Clone())).Where(client => client.Pid > 0 && client.Pid != connected).DistinctBy(client => client.Pid).ToArray();
    }
    public static int ConnectingPid(JsonElement state) => (int)state.Field("connectingClient").Number("pid");
    public static bool IsEndgame(string phase) => phase is "WaitingForStats" or "PreEndOfGame" or "EndOfGame";
    public static string ServerId(string region, string platform)
    {
        string value = region.ToUpperInvariant(); if (value == "TENCENT") return "TENCENT_" + platform.ToUpperInvariant();
        return value switch { "NA" => "NA1", "BR" => "BR1", "TR" => "TR1", "LAN" => "LA1", "LAS" => "LA2", "OCE" => "OC1", "EUW1" => "EUW", "JP1" => "JP", _ => value };
    }
    public static string ServerName(JsonElement sgp, string region, string platform, string locale)
    {
        string id = ServerId(region, platform);
        return sgp.Field("leagueServers").Field("serverNames").Field(locale).Text(id, id);
    }
    public static ConnectionClient? Choose(IEnumerable<ConnectionClient> clients, int previous, int connecting) => clients.FirstOrDefault(client => client.Pid == connecting) ?? clients.FirstOrDefault(client => client.Pid == previous) ?? clients.FirstOrDefault();
    public static bool ShouldPeek(DateTimeOffset? lastPeek, DateTimeOffset now) => lastPeek is null || now - lastPeek >= TimeSpan.FromSeconds(10);
}
