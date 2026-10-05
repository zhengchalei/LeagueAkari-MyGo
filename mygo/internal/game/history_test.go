package game

import (
	"encoding/json"
	"strings"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestCompactHistoryRetainsRendererStatsAndRemovesMissions(t *testing.T) {
	data := json.RawMessage(`{"games":[{"metadata":{"matchId":"NJ100_99"},"json":{"gameId":99,"gameMode":"KIWI","participants":[{"puuid":"self","kills":10,"missions":{"unused":"progression-payload"},"challenges":{"kda":5.5,"teamDamagePercentage":0.2},"item0":1001,"augments":[123]}]}}]}`)
	games, err := compactHistory(data, true)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := json.Marshal(games)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(wire), "missions") || strings.Contains(string(wire), "progression-payload") {
		t.Fatal("unused progression still retained")
	}
	var decoded []any
	json.Unmarshal(wire, &decoded)
	game := client.Map(decoded[0])
	summary := client.Map(client.Map(game["data"])["json"])
	participant := client.Map(client.List(summary["participants"])[0])
	if client.Number(game["gameId"]) != 99 || summary["gameMode"] != "KIWI" || client.Number(participant["kills"]) != 10 || client.Map(participant["challenges"])["kda"] != 5.5 || client.Number(participant["item0"]) != 1001 {
		t.Fatal("summary fields needed by renderer were lost")
	}
}
