package game

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
)

func playerStore(t *testing.T) *player.Service {
	t.Helper()
	p, err := player.New(filepath.Join(t.TempDir(), "players.sqlite"), nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	return p
}

func TestSavedInfoUpdatesImmediatelyAndEndOfGameRecordsOnce(t *testing.T) {
	b := newFake()
	p := playerStore(t)
	s := New(b, nil)
	s.SetPlayerStore(p)
	defer s.Close()
	s.Refresh(context.Background())
	if _, err := p.Call("updatePlayerTag", []any{map[string]any{"puuid": "teammate", "selfPuuid": "self", "region": "TENCENT", "rsoPlatformId": "NJ100", "tag": "合作愉快"}}); err != nil {
		t.Fatal(err)
	}
	info := client.Map(decodedRaw(client.Map(s.GetAll()["savedInfo"])["teammate"]))
	if info["tag"] != "合作愉快" {
		t.Fatal("savedInfo was not synced", info)
	}
	client.Map(b.state["champSelect"])["session"] = nil
	client.Map(b.state["gameflow"])["phase"] = "PreEndOfGame"
	data := client.Map(client.Map(client.Map(b.state["gameflow"])["session"])["gameData"])
	client.Map(data["queue"])["type"] = "ARAM_UNRANKED_5x5"
	data["teamOne"] = []any{map[string]any{"puuid": "self", "teamParticipantId": 7}, map[string]any{"puuid": "teammate", "teamParticipantId": 7}}
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	client.Map(b.state["gameflow"])["phase"] = "EndOfGame"
	s.Refresh(context.Background())
	query := player.Query{Puuid: "teammate", SelfPuuid: "self"}
	games, err := p.QueryEncounteredGames(query)
	if err != nil {
		t.Fatal(err)
	}
	if games.(map[string]any)["total"] != int64(1) {
		t.Fatal(games)
	}
	info = client.Map(decodedRaw(client.Map(s.GetAll()["savedInfo"])["teammate"]))
	if info["tag"] != "合作愉快" || info["lastMetAt"] == nil {
		t.Fatal("encounter erased tag", info)
	}
	groups := client.Map(s.State()["teamParticipantGroups"])
	if len(client.List(groups["7"])) != 2 {
		t.Fatal(groups)
	}
	if _, err := p.Call("deleteSavedPlayer", []any{map[string]any{"puuid": "teammate", "selfPuuid": "self"}}); err != nil {
		t.Fatal(err)
	}
	if client.Map(s.GetAll()["savedInfo"])["teammate"] != nil {
		t.Fatal("deleted tag info retained")
	}
}

func TestHistoricalDraftOverridesLiveAndCannotWriteEncounters(t *testing.T) {
	b := newFake()
	p := playerStore(t)
	s := New(b, nil)
	s.SetPlayerStore(p)
	defer s.Close()
	draft := map[string]any{"gameModeKind": "normal", "queueId": 2400, "puuid": "self", "teams": map[string]any{"TEAM-100": []any{"self", "historical"}}, "championSelections": map[string]any{"historical": 147}, "positions": map[string]any{"historical": map[string]any{"selected": "middle", "primary": "middle", "secondary": "top"}}}
	if err := s.SetDraft(draft); err != nil {
		t.Fatal(err)
	}
	state := s.State()
	if client.Map(state["queryStage"])["phase"] != "draft" || client.Map(client.Map(state["positionAssignments"])["historical"])["position"] != "MIDDLE" {
		t.Fatal(state)
	}
	client.Map(b.state["gameflow"])["phase"] = "EndOfGame"
	client.Map(b.state["champSelect"])["session"] = nil
	s.Refresh(context.Background())
	if client.Map(s.State()["queryStage"])["phase"] != "draft" {
		t.Fatal("live game overwrote draft")
	}
	games, err := p.QueryEncounteredGames(player.Query{Puuid: "historical", SelfPuuid: "self"})
	if err != nil || games.(map[string]any)["total"] != int64(0) {
		t.Fatal("simulation recorded encounter", games, err)
	}
	s.ClearDraft()
	client.Map(b.state["gameflow"])["phase"] = "ChampSelect"
	client.Map(b.state["champSelect"])["session"] = map[string]any{"myTeam": []any{map[string]any{"puuid": "self"}, map[string]any{"puuid": "teammate"}}}
	s.Refresh(context.Background())
	if client.Map(s.State()["queryStage"])["phase"] != "champ-select" || client.Map(s.State()["draft"]) != nil && len(client.Map(s.State()["draft"])) > 0 {
		t.Fatal("draft did not clear")
	}
	if client.Map(s.GetAll()["matchHistory"])["historical"] != nil {
		t.Fatal("historical cache retained after clear")
	}
}
