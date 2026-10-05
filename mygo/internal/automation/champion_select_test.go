package automation

import (
	"context"
	"fmt"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func championSession() selectSession {
	return decodeValue[selectSession](map[string]any{
		"id": "session", "gameId": 100, "localPlayerCellId": 0,
		"myTeam":  []any{map[string]any{"cellId": 0, "championId": 0, "assignedPosition": ""}},
		"actions": []any{[]any{map[string]any{"id": 1, "actorCellId": 0, "type": "pick", "completed": false, "isInProgress": true, "championId": 0}}},
		"timer":   map[string]any{"phase": "BAN_PICK", "isInfinite": true},
	})
}

func configurePick(t *testing.T, store *settings.Store, patch map[string]any) {
	t.Helper()
	config := map[string]any{"enabled": true, "strategy": "show-and-lock-in", "delaySeconds": 0, "champions": map[string]any{"default": []int{99, 1, 2}}, "benchSwapAccumulatedDelaySeconds": 0}
	for key, value := range patch {
		config[key] = value
	}
	setSetting(t, store, settings.SelectNamespace, "pickConfig", map[string]any{"ARAM": config})
}

func TestChampionPickShowsThenLocksAvailableChampionOnce(t *testing.T) {
	store := newStore(t)
	configurePick(t, store, nil)
	session := championSession()
	var patches []map[string]any
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM", "type": "ARAM_UNRANKED_5x5"}}}, nil
		case "/lol-champ-select/v1/session":
			return session, nil
		case "/lol-champ-select/v1/pickable-champion-ids":
			return []int{1, 2}, nil
		case "/lol-champ-select/v1/all-grid-champions":
			return []any{map[string]any{"id": 1, "selectionStatus": map[string]any{}}, map[string]any{"id": 2, "selectionStatus": map[string]any{}}}, nil
		case "/lol-champ-select/v1/session/actions/1":
			payload := body.(map[string]any)
			patches = append(patches, payload)
			session.Actions[0][0].ChampionID = payload["championId"].(int)
			session.Actions[0][0].Completed = payload["completed"].(bool)
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	for range 3 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(patches) != 2 || patches[0]["championId"] != 1 || patches[0]["completed"] != false || patches[1]["completed"] != true {
		t.Fatalf("unexpected patches %v", patches)
	}
}

func TestChampionSelectionIgnoresTeammateActionsAndTemporaryDisable(t *testing.T) {
	store := newStore(t)
	configurePick(t, store, nil)
	session := championSession()
	session.Actions[0][0].ActorCellID = 2
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method != "GET" {
			writes++
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
		case "/lol-champ-select/v1/session":
			return session, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	session.Actions[0][0].ActorCellID = 0
	runner.SetTemporarilyDisabled(true)
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 0 {
		t.Fatal("changed other player or ignored temporary disable")
	}
}

func TestSubsetPickCannotSelectChampionOutsideItsCards(t *testing.T) {
	store := newStore(t)
	configurePick(t, store, map[string]any{"strategy": "lock-in-immediately"})
	session := championSession()
	session.AllowSubsetChampionPicks = true
	chosen := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
		case "/lol-champ-select/v1/session":
			return session, nil
		case "/lol-champ-select/v1/pickable-champion-ids":
			return []int{1, 2}, nil
		case "/lol-lobby-team-builder/champ-select/v1/subset-champion-list":
			return []int{2}, nil
		case "/lol-champ-select/v1/all-grid-champions":
			return []any{map[string]any{"id": 1}, map[string]any{"id": 2}}, nil
		case "/lol-champ-select/v1/session/actions/1":
			chosen = body.(map[string]any)["championId"].(int)
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if chosen != 2 {
		t.Fatalf("selected outside card pool: %d", chosen)
	}
}

func TestBenchOnlySwapsToHigherPriorityAndRechecksAvailability(t *testing.T) {
	store := newStore(t)
	configurePick(t, store, map[string]any{"champions": map[string]any{"default": []int{1, 2}}, "benchSelectFirstAvailableChampion": true, "benchSwapAccumulatedDelaySeconds": 2})
	session := championSession()
	session.Actions = nil
	session.BenchEnabled = true
	session.MyTeam[0].ChampionID = 2
	session.BenchChampions = append(session.BenchChampions, struct {
		ChampionID int `json:"championId"`
	}{ChampionID: 1})
	now := time.Unix(100, 0)
	swaps := 0
	freshReads := 0
	disappear := false
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
		case "/lol-champ-select/v1/session":
			freshReads++
			copy := session
			if disappear && freshReads > 1 {
				copy.BenchChampions = nil
			}
			return copy, nil
		case "/lol-champ-select/v1/pickable-champion-ids":
			return []int{1, 2}, nil
		case "/lol-champ-select/v1/session/bench/swap/1":
			swaps++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	runner.now = func() time.Time { return now }
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if swaps != 0 {
		t.Fatal("bench delay ignored")
	}
	now = now.Add(3 * time.Second)
	freshReads = 0
	disappear = true
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if swaps != 0 {
		t.Fatal("swapped champion no longer in bench")
	}
	freshReads = 0
	disappear = false
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	now = now.Add(3 * time.Second)
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if swaps != 1 {
		t.Fatalf("expected preferred champion swap, got %d", swaps)
	}
	session.MyTeam[0].ChampionID = 1
	session.BenchChampions[0].ChampionID = 2
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if swaps != 1 {
		t.Fatal("downgraded to lower priority hero")
	}
}

func TestOriginalSelectGroupsMatchModeAndCustomFlag(t *testing.T) {
	store := newStore(t)
	runner := New(fakeClient{}, store, nil)
	defer runner.Close()
	groups := decodeValue[[]SelectGroup]([]any{map[string]any{"groupId": "normal-rank", "isCustom": false, "targetGameModes": []any{map[string]any{"gameMode": "CLASSIC", "queueTypes": []string{"RANKED_SOLO_5x5"}}}, "positions": []string{"top"}}})
	runner.SetSelectGroups(groups)
	game := decodeValue[gameSession](map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "CLASSIC", "type": "RANKED_SOLO_5x5"}}})
	if runner.activeGroup(game, false) != "normal-rank" || runner.activeGroup(game, true) != "" {
		t.Fatal("group matching lost mode/custom scope")
	}
	state := runner.State()[settings.SelectNamespace].(map[string]any)
	if len(state["groups"].([]any)) != 1 {
		t.Fatal("original UI cannot see groups")
	}
}

func TestAutoBanRespectsBannableListAndCompletedOwnAction(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.SelectNamespace, "banConfig", map[string]any{"ARAM": map[string]any{"enabled": true, "champions": map[string]any{"default": []int{99, 1}}, "strategy": "lock-in-immediately"}})
	session := championSession()
	session.Actions[0][0].Type = "ban"
	chosen, patches := 0, 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
		case "/lol-champ-select/v1/session":
			return session, nil
		case "/lol-champ-select/v1/bannable-champion-ids":
			return []int{1}, nil
		case "/lol-champ-select/v1/session/actions/1":
			chosen = body.(map[string]any)["championId"].(int)
			session.Actions[0][0].Completed = true
			patches++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if chosen != 1 || patches != 1 {
		t.Fatalf("invalid or duplicate ban chosen=%d patches=%d", chosen, patches)
	}
}
