package game

import (
	"context"
	"reflect"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestRedSideChampSelectCacheDoesNotDuplicateLivePlayers(t *testing.T) {
	for _, partial := range []bool{false, true} {
		name := "complete roster"
		if partial {
			name = "missing teammate"
		}
		t.Run(name, func(t *testing.T) {
			b := newFake()
			s := New(b, nil)
			s.collect(context.Background(), b.state)
			client.Map(b.state["champSelect"])["session"] = nil
			client.Map(b.state["gameflow"])["phase"] = "InProgress"
			data := client.Map(client.Map(client.Map(b.state["gameflow"])["session"])["gameData"])
			data["teamOne"] = []any{map[string]any{"puuid": "enemy", "championId": 23}}
			red := []any{map[string]any{"puuid": "self", "championId": 103}}
			if !partial {
				red = append(red, map[string]any{"puuid": "teammate", "championId": 147})
			}
			data["teamTwo"] = red
			_, teams, selections, _ := s.collect(context.Background(), b.state)
			if !reflect.DeepEqual(teams["TEAM-100"], []any{"enemy"}) || !reflect.DeepEqual(teams["TEAM-200"], []any{"self", "teammate"}) {
				t.Fatalf("red players duplicated or assigned to blue: %v", teams)
			}
			if client.Number(selections["teammate"]) != 147 {
				t.Fatal("cached champion selection was lost")
			}
		})
	}
}

func TestAdditionalRosterDoesNotMoveExistingLivePlayers(t *testing.T) {
	b := configurable()
	client.Map(b.state["champSelect"])["session"] = nil
	client.Map(b.state["gameflow"])["phase"] = "InProgress"
	data := client.Map(client.Map(client.Map(b.state["gameflow"])["session"])["gameData"])
	data["teamOne"] = []any{map[string]any{"puuid": "enemy"}}
	data["teamTwo"] = []any{map[string]any{"puuid": "self"}}
	b.gsm = map[string]any{
		"teamOne": []any{map[string]any{"puuid": "self"}},
		"teamTwo": []any{map[string]any{"puuid": "enemy"}, map[string]any{"puuid": "teammate", "championId": 147}},
	}
	s := New(b, nil)
	_, teams, _, _ := s.collect(context.Background(), b.state)
	if !reflect.DeepEqual(teams["TEAM-100"], []any{"enemy"}) || !reflect.DeepEqual(teams["TEAM-200"], []any{"self", "teammate"}) {
		t.Fatalf("additional roster duplicated live players: %v", teams)
	}
}
