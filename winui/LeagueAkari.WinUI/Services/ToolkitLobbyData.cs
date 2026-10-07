using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public record ToolkitQueue(int Id, string Name, bool Eligible);
public record SwarmChampion(int Id, string Name, bool Specific);
public record SwarmMap(string Name, string ContentId, int ItemId);

public static class ToolkitLobbyData
{
    public static IEnumerable<JsonElement> Values(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(entry => entry.Value) : value.Items();
    public static ToolkitQueue[] Queues(JsonElement definitions, JsonElement self, JsonElement party)
    {
        var own = self.Items().Select(queue => (int)queue.Number("queueId")).ToHashSet();
        var group = party.Items().Select(queue => (int)queue.Number("queueId")).ToHashSet();
        return Values(definitions).Select(queue => new ToolkitQueue((int)queue.Number("id"), queue.Text("name"), own.Contains((int)queue.Number("id")) && group.Contains((int)queue.Number("id")))).OrderByDescending(queue => queue.Eligible).ToArray();
    }
    public static bool IsSwarm(JsonElement client, JsonElement lobby) => client.Text("connectionState") == "connected" && lobby.Field("lobby").Field("gameConfig").Text("gameMode") == "STRAWBERRY";
    public static SwarmChampion[] Champions(JsonElement definitions) => Values(definitions)
        .Where(hero => hero.Number("id") > 0).Select(hero => new SwarmChampion((int)hero.Number("id"), hero.Text("name"), hero.Number("id") is >= 3000 and < 4000))
        .OrderByDescending(hero => hero.Specific).ThenBy(hero => hero.Name, StringComparer.CurrentCulture).ToArray();
    public static SwarmMap[] Maps(JsonElement data) => data.Items().FirstOrDefault().Field("MapDisplayInfoList").Items().Select(item => item.Field("value"))
        .Select(item => new SwarmMap(item.Text("Name"), item.Field("Map").Text("ContentId"), (int)item.Field("Map").Number("ItemId"))).ToArray();
    public static object Slots(int champion, SwarmMap? map, int? difficulty) => new[] { new { championId = champion, positionPreference = "UNSELECTED", spell1 = map?.ItemId is > 0 ? map.ItemId : 1, spell2 = difficulty is > 0 ? difficulty.Value : 1 } };
    public static bool ValidQueue(double id) => double.IsFinite(id) && id >= 1 && id <= int.MaxValue && id == Math.Truncate(id);
}
