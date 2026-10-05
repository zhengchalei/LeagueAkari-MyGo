package game

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type fakeBackend struct {
	mu       sync.Mutex
	state    map[string]any
	requests []string
}

func (b *fakeBackend) State() map[string]any { return b.state }
func (b *fakeBackend) CurrentServer() string { return "TENCENT_NJ100" }
func (b *fakeBackend) JSON(ctx context.Context, method, path string, body any) (any, error) {
	if strings.Contains(path, "ranked-stats") {
		return map[string]any{"queues": []any{map[string]any{"tier": "GOLD", "queueType": "RANKED_SOLO_5x5"}}}, nil
	}
	if strings.Contains(path, "champion-mastery") {
		return []any{map[string]any{"championId": float64(103), "championPoints": float64(10000)}}, nil
	}
	return map[string]any{"puuid": "self", "gameName": "玩家"}, nil
}
func (b *fakeBackend) SGPJSON(ctx context.Context, server, token, method, path string, body any) (any, error) {
	b.mu.Lock()
	b.requests = append(b.requests, path)
	b.mu.Unlock()
	if strings.Contains(path, "DETAILS") {
		return map[string]any{"json": map[string]any{"frames": []any{}}}, nil
	}
	if !strings.Contains(path, "count=50") {
		return nil, fmt.Errorf("summary load count must be 50")
	}
	games := []any{}
	for i := 0; i < 50; i++ {
		games = append(games, map[string]any{"metadata": map[string]any{}, "json": map[string]any{"gameId": float64(i + 1), "participants": []any{}}})
	}
	return map[string]any{"games": games}, nil
}

func newFake() *fakeBackend {
	return &fakeBackend{state: map[string]any{"state": map[string]any{"connectionState": "connected"}, "summoner": map[string]any{"me": map[string]any{"puuid": "self"}}, "gameflow": map[string]any{"phase": "ChampSelect", "session": map[string]any{"gameData": map[string]any{"gameId": float64(99), "queue": map[string]any{"id": float64(2400), "gameMode": "KIWI"}}}}, "champSelect": map[string]any{"session": map[string]any{"gameId": float64(99), "myTeam": []any{map[string]any{"puuid": "self", "championId": float64(103)}, map[string]any{"puuid": "teammate", "championId": float64(147)}}}}}}
}

func TestRefreshSummariesWithoutTimelinePreload(t *testing.T) {
	b := newFake()
	s := New(b, nil)
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	all := s.GetAll()
	for _, key := range []string{"matchHistory", "summoner", "rankedStats", "savedInfo", "championMastery", "gameDetails", "additionalGames"} {
		if all[key] == nil {
			t.Fatalf("getAll missing %s", key)
		}
	}
	history := client.Map(decodedRaw(client.Map(all["matchHistory"])["self"]))
	games := client.List(history["data"])
	if len(games) != 50 || client.String(client.Map(games[0])["source"]) != "sgp" {
		t.Fatal("history wrapper does not retain 50 summaries")
	}
	for _, path := range b.requests {
		if strings.Contains(path, "DETAILS") {
			t.Fatal("refresh preloaded a timeline")
		}
	}
	if len(client.Map(all["gameDetails"])) != 0 {
		t.Fatal("timelines retained without user request")
	}
	if client.String(client.Map(s.State()["queryStage"])["phase"]) != "champ-select" {
		t.Fatal("selection stage is inaccurate")
	}
	initialRequests := len(b.requests)
	s.Refresh(context.Background())
	if len(b.requests) != initialRequests {
		t.Fatal("unchanged player histories were fetched again")
	}
}

func TestChampSelectRosterSurvivesGameStart(t *testing.T) {
	b := newFake()
	s := New(b, nil)
	s.Refresh(context.Background())
	b.state["champSelect"] = map[string]any{"session": nil}
	client.Map(b.state["gameflow"])["phase"] = "GameStart"
	s.Refresh(context.Background())
	state := s.State()
	if client.String(client.Map(state["queryStage"])["phase"]) != "in-game" || len(client.List(client.Map(state["teams"])["TEAM-100"])) != 2 {
		t.Fatal("roster was lost during game start")
	}
}

func TestTimelineCacheKeepsOnlyFourUserRequestedGames(t *testing.T) {
	b := newFake()
	s := New(b, nil)
	for id := int64(1); id <= 5; id++ {
		if _, err := s.GetTimeline(context.Background(), "", id); err != nil {
			t.Fatal(err)
		}
	}
	if len(client.Map(s.GetAll()["gameDetails"])) != 4 {
		t.Fatal("timeline cache exceeded four games")
	}
	before := len(b.requests)
	s.GetTimeline(context.Background(), "", 5)
	if len(b.requests) != before {
		t.Fatal("cached timeline fetched again")
	}
	s.GetTimeline(context.Background(), "", 1)
	if len(b.requests) != before+1 {
		t.Fatal("oldest timeline was not evicted")
	}
}

func TestActiveGameHistoryRemainsCachedUntilNextGame(t *testing.T) {
	b := newFake()
	s := New(b, nil)
	client.Map(b.state["gameflow"])["phase"] = "InProgress"
	client.Map(b.state["champSelect"])["session"] = nil
	client.Map(client.Map(b.state["gameflow"])["session"])["gameData"].(map[string]any)["teamOne"] = []any{map[string]any{"puuid": "self", "championId": 103}}
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	initial := len(b.requests)
	if initial == 0 {
		t.Fatal("no history loaded")
	}
	s.loaded["self"] = time.Now().Add(-10 * time.Minute)
	s.Refresh(context.Background())
	if len(b.requests) != initial {
		t.Fatal("unchanged in-game history reloaded")
	}
	client.Map(client.Map(client.Map(b.state["gameflow"])["session"])["gameData"])["gameId"] = float64(100)
	s.Refresh(context.Background())
	if len(b.requests) <= initial {
		t.Fatal("next game reused stale history")
	}
}
