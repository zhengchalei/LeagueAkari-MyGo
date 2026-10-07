using System.Text.Json;

namespace LeagueAkari.WinUI.Services;

public sealed record RecentPlayer(string Puuid, string Name, string Tag, int ProfileIconId, bool IsOpponent, int Games, int Wins, int Losses);
public sealed record EncounterPlayer(string Puuid, int ChampionId, double Kills, double Deaths, double Assists, bool Win, int Placement)
{
    public string Position { get; init; } = "";
    public string Result { get; init; } = "";
}
public sealed record EncounterRecord(long Id, long GameId, string SelfPuuid, string Puuid, string QueueType, DateTimeOffset? RecordedAt)
{
    public static EncounterRecord Parse(JsonElement value) => new((long)value.Number("id"), (long)value.Number("gameId"), value.Text("selfPuuid"), value.Text("puuid"), value.Text("queueType"), DateTimeOffset.TryParse(value.Text("updateAt"), out var date) ? date : null);
}
public sealed record EncounterGame(EncounterRecord Record, JsonElement Summary, string QueueName, DateTimeOffset? PlayedAt, bool? IsOpponent, EncounterPlayer? Self, EncounterPlayer? Target);

public static class EncounterData
{
    private const string EmptyPuuid = "00000000-0000-0000-0000-000000000000";
    public static RecentPlayer[] RecentPlayers(IEnumerable<JsonElement> games, string puuid)
    {
        var relationships = new Dictionary<(string Puuid, bool Enemy), RecentPlayer>();
        foreach (var raw in games)
        {
            var game = MatchData.Game(raw); var participants = MatchData.Participants(game); var self = participants.FirstOrDefault(p => p.Puuid == puuid);
            if (self is null) continue;
            foreach (var target in participants.Where(p => p.Puuid.Length > 0 && p.Puuid != EmptyPuuid && p.Puuid != puuid))
            {
                bool enemy = target.TeamKey != self.TeamKey;
                var key = (target.Puuid, enemy);
                if (!relationships.TryGetValue(key, out var row)) row = new(target.Puuid, target.Name, target.Tag, ProfileIcon(game, target), enemy, 0, 0, 0);
                bool win = enemy ? target.WinResult != "win" : target.WinResult == "win";
                relationships[key] = row with { Games = row.Games + 1, Wins = row.Wins + (win ? 1 : 0), Losses = row.Losses + (win ? 0 : 1) };
            }
        }
        return relationships.Values.Where(p => p.Games >= 2).OrderBy(p => p.IsOpponent).ThenByDescending(p => p.Games).ThenByDescending(p => p.Wins).ToArray();
    }
    private static int ProfileIcon(JsonElement game, MatchParticipant participant)
    {
        var identity = game.Field("participantIdentities").Items().FirstOrDefault(i => i.Number("participantId") == participant.Id).Field("player");
        return (int)participant.Raw.Number("profileIcon", participant.Raw.Number("profileIconId", identity.Number("profileIcon", identity.Number("profileIconId"))));
    }
    public static EncounterGame? Project(EncounterRecord record, JsonElement raw, Func<int, string>? queueName = null)
    {
        if (raw.ValueKind != JsonValueKind.Object) return new(record, default, record.QueueType, record.RecordedAt, null, null, null);
        var game = MatchData.Game(raw); var participants = MatchData.Participants(game);
        var self = participants.FirstOrDefault(p => p.Puuid == record.SelfPuuid); var target = participants.FirstOrDefault(p => p.Puuid == record.Puuid);
        // Upstream omits a resolved game if either side's identity is absent.
        if (self is null || target is null) return null;
        int queue = (int)game.Number("queueId"); var date = MatchData.Creation(game);
        EncounterPlayer Player(MatchParticipant p) => new(p.Puuid, p.ChampionId, p.Kills, p.Deaths, p.Assists, p.WinResult == "win", (int)p.Num("subteamPlacement")) { Position = p.Position, Result = p.WinResult };
        return new(record, raw.Clone(), queueName?.Invoke(queue) ?? MatchData.QueueLabel(queue), date == DateTimeOffset.MinValue ? null : date, self.TeamKey != target.TeamKey, Player(self), Player(target));
    }
    public static bool CanLoadSaved(string target, string self, bool crossRegion) => !crossRegion && target.Length > 0 && self.Length > 0 && target != self;
}
