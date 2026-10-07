using System.Text.Json;

// In-memory claimable entries. These routes never reach a game client.
public sealed class FixtureRewards
{
    private readonly HashSet<string> _claimed = [];
    private bool _failOnce = Environment.GetEnvironmentVariable("WINUI_FIXTURE_REWARD_FAIL_ONCE") == "1";
    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);
    public object? Request(string method, string path)
    {
        if (method != "GET" && (path.StartsWith("/lol-missions/v1/player/") || path.StartsWith("/lol-rewards/v1/grants/") || path.StartsWith("/lol-event-hub/v1/events/")))
        {
            if (_failOnce) { _failOnce = false; throw new InvalidOperationException("模拟领取失败，请重试"); }
            string id = path.Split('/')[4]; _claimed.Add(id); return new { simulated = true };
        }
        if (method != "GET") return null;
        if (path == "/lol-missions/v1/missions") return Json("""[{"id":"mission-1","internalName":"测试任务一","status":"SELECT_REWARDS","rewardStrategy":{"selectMaxGroupCount":1},"rewards":[{"rewardGroup":"mission-blue","description":"蓝色精萃","iconUrl":"/fixture/reward.png"},{"rewardGroup":"mission-key","description":"钥匙碎片","iconUrl":"/fixture/reward.png"}]},{"id":"mission-2","internalName":"测试任务二","status":"SELECT_REWARDS","rewards":[{"rewardGroup":"mission-orange","description":"橙色精萃","iconUrl":"/fixture/reward.png"}]}]""").EnumerateArray().Where(item => !_claimed.Contains(item.GetProperty("id").GetString()!)).ToArray();
        if (path.StartsWith("/lol-rewards/v1/grants")) return Json("""[{"info":{"id":"grant-1","status":"PENDING_SELECTION"},"rewardGroup":{"id":"gift-group","localizations":{"title":"测试礼包"},"selectionStrategyConfig":{"maxSelectionsAllowed":1},"rewards":[{"id":"gift-one","localizations":{"title":"英雄碎片"},"media":{"iconUrl":"/fixture/reward.png"}},{"id":"gift-two","localizations":{"title":"皮肤碎片"},"media":{"iconUrl":"/fixture/reward.png"}}]}}]""").EnumerateArray().Where(item => !_claimed.Contains(item.GetProperty("info").GetProperty("id").GetString()!)).ToArray();
        if (path == "/lol-event-hub/v1/events") return _claimed.Contains("event-1") ? Array.Empty<object>() : Json("""[{"eventId":"event-1","eventInfo":{"eventName":"测试活动通行证","unclaimedRewardCount":2}}]""");
        if (path.EndsWith("/reward-track/items")) return Json("""[{"rewardOptions":[{"state":"Unselected","rewardGroupId":"track-one","rewardName":"通行证奖励","thumbIconPath":"/fixture/reward.png"}]}]""");
        if (path.EndsWith("/reward-track/bonus-items")) return Json("""[{"rewardOptions":[{"state":"Unselected","rewardGroupId":"bonus-one","rewardName":"额外奖励","thumbIconPath":"/fixture/reward.png"}]}]""");
        return null;
    }
}
