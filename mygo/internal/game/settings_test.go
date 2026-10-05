package game

import (
	"context"
	"errors"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type configurableBackend struct {
	*fakeBackend
	callMu         sync.Mutex
	calls          []string
	active, peak   int
	failLCUHistory bool
	gsm            map[string]any
	chatMessages   []map[string]any
}

func configurable() *configurableBackend { return &configurableBackend{fakeBackend: newFake()} }
func (b *configurableBackend) addCall(source, path string) {
	b.callMu.Lock()
	b.calls = append(b.calls, source+":"+path)
	b.callMu.Unlock()
}
func (b *configurableBackend) JSON(ctx context.Context, method, path string, body any) (any, error) {
	b.addCall("lcu", path)
	if path == "/lol-chat/v1/conversations" {
		return []any{map[string]any{"type": "championSelect", "id": "fixture-room"}}, nil
	}
	if strings.HasSuffix(path, "/messages") {
		b.chatMessages = append(b.chatMessages, client.Map(body))
		return map[string]any{}, nil
	}
	if strings.Contains(path, "/matches?") {
		if b.failLCUHistory {
			return nil, errors.New("fixture LCU history unavailable")
		}
		games := []any{}
		for i := 1; i <= 5; i++ {
			games = append(games, map[string]any{"gameId": i, "participants": []any{}})
		}
		return map[string]any{"games": map[string]any{"games": games}}, nil
	}
	if strings.Contains(path, "game-timelines") {
		return map[string]any{"frames": []any{}}, nil
	}
	return b.fakeBackend.JSON(ctx, method, path, body)
}
func (b *configurableBackend) SGPJSON(ctx context.Context, server, token, method, path string, body any) (any, error) {
	b.addCall("sgp", path)
	if strings.Contains(path, "/gsm/") {
		return map[string]any{"game": b.gsm}, nil
	}
	if strings.Contains(path, "DETAILS") {
		return map[string]any{"json": map[string]any{"frames": []any{}}}, nil
	}
	if strings.Contains(path, "/SUMMARY?") {
		b.callMu.Lock()
		b.active++
		b.peak = max(b.peak, b.active)
		b.callMu.Unlock()
		defer func() { b.callMu.Lock(); b.active--; b.callMu.Unlock() }()
		time.Sleep(10 * time.Millisecond)
		games := []any{}
		for i := 1; i <= 5; i++ {
			games = append(games, map[string]any{"json": map[string]any{"gameId": i, "participants": []any{}}})
		}
		return map[string]any{"games": games}, nil
	}
	if strings.HasSuffix(path, "/SUMMARY") {
		return map[string]any{"json": map[string]any{"gameId": 777, "participants": []any{}}}, nil
	}
	return nil, errors.New("unrecognized fixture request: " + path)
}
func gameSettings(t *testing.T) *settings.Store {
	t.Helper()
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	store.ApplyDefaults(map[string]map[string]any{"ongoing-game-main": {"enabled": true, "queryInLobbyPhase": true, "concurrency": 2, "matchHistoryLoadCount": 5, "gameDetailsLoadCount": 0}, "app-common-main": {"preferredLolSource": "sgp"}})
	return store
}
func setGameSetting(t *testing.T, store *settings.Store, key string, value any) {
	t.Helper()
	if err := store.Set("ongoing-game-main", key, value); err != nil {
		t.Fatal(err)
	}
}

func TestDisabledAndLobbyExcludedDoNotQueryPlayers(t *testing.T) {
	for _, scenario := range []string{"disabled", "lobby-excluded"} {
		t.Run(scenario, func(t *testing.T) {
			b := configurable()
			store := gameSettings(t)
			s := New(b, nil)
			s.SetSettings(store)
			if scenario == "disabled" {
				setGameSetting(t, store, "enabled", false)
			} else {
				client.Map(b.state["champSelect"])["session"] = nil
				client.Map(b.state["gameflow"])["phase"] = "Lobby"
				b.state["lobby"] = map[string]any{"lobby": map[string]any{"gameConfig": map[string]any{"queueId": 2400}, "members": []any{map[string]any{"puuid": "self"}}}}
				setGameSetting(t, store, "queryInLobbyPhase", false)
			}
			if err := s.Refresh(context.Background()); err != nil {
				t.Fatal(err)
			}
			if len(b.calls) != 0 || client.Map(s.State()["queryStage"])["phase"] != "unavailable" {
				t.Fatal("excluded phase queried players", b.calls)
			}
		})
	}
}

func TestConfiguredConcurrencyAndSourceAreConsumed(t *testing.T) {
	b := configurable()
	store := gameSettings(t)
	s := New(b, nil)
	s.SetSettings(store)
	team := []any{}
	for _, id := range []string{"self", "one", "two", "three", "four", "five"} {
		team = append(team, map[string]any{"puuid": id})
	}
	client.Map(client.Map(b.state["champSelect"])["session"])["myTeam"] = team
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	if b.peak != 2 {
		t.Fatal("concurrency was not applied", b.peak)
	}
	if err := store.Set("app-common-main", "preferredLolSource", "lcu"); err != nil {
		t.Fatal(err)
	}
	b.calls = nil
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	for _, call := range b.calls {
		if strings.Contains(call, "sgp:") {
			t.Fatal("LCU preference ignored", call)
		}
	}
	if client.Map(decodedRaw(client.Map(s.GetAll()["matchHistory"])["self"]))["source"] != "lcu" {
		t.Fatal("wrong source wrapper")
	}
	b.failLCUHistory = true
	if err := s.ReloadPlayerWithOptions(context.Background(), "self", map[string]any{"includes": []any{"matchHistory"}}); err != nil {
		t.Fatal(err)
	}
	if client.Map(decodedRaw(client.Map(s.GetAll()["matchHistory"])["self"]))["source"] != "sgp" {
		t.Fatal("failed preferred source did not fall back")
	}
}

func TestDetailsDefaultZeroExplicitPrefetchAndExitBoundary(t *testing.T) {
	b := configurable()
	store := gameSettings(t)
	s := New(b, nil)
	s.SetSettings(store)
	s.Refresh(context.Background())
	if len(client.Map(s.GetAll()["gameDetails"])) != 0 {
		t.Fatal("default preloaded details")
	}
	setGameSetting(t, store, "gameDetailsLoadCount", 2)
	s.Refresh(context.Background())
	if len(client.Map(s.GetAll()["gameDetails"])) != 2 {
		t.Fatal("explicit detail count ignored")
	}
	count := 0
	for _, path := range b.calls {
		if strings.Contains(path, "DETAILS") {
			count++
		}
	}
	if count != 2 {
		t.Fatal("shared match details were fetched twice", count)
	}
	for id := int64(10); id < 15; id++ {
		if _, err := s.GetTimeline(context.Background(), "", id); err != nil {
			t.Fatal(err)
		}
	}
	if len(client.Map(s.GetAll()["gameDetails"])) != 6 {
		t.Fatal("manual four and configured two are not separately bounded")
	}
	setGameSetting(t, store, "gameDetailsLoadCount", 0)
	s.Refresh(context.Background())
	if len(client.Map(s.GetAll()["gameDetails"])) != 4 {
		t.Fatal("disabled prefetch retained data")
	}
	client.Map(b.state["champSelect"])["session"] = nil
	client.Map(b.state["gameflow"])["phase"] = "None"
	s.Refresh(context.Background())
	if len(client.Map(s.GetAll()["gameDetails"])) != 0 {
		t.Fatal("exit retained previous game details")
	}
}

func TestSavedInfoReloadIsLocalAndEncounterSummaryIsCached(t *testing.T) {
	b := configurable()
	p := playerStore(t)
	s := New(b, nil)
	s.SetPlayerStore(p)
	defer s.Close()
	p.RecordEncounter(player.Encounter{GameID: 777, Puuid: "teammate", SelfPuuid: "self", Region: "TENCENT", RsoPlatformID: "NJ100", QueueType: "ARAM"})
	s.Refresh(context.Background())
	entry := client.Map(decodedRaw(client.Map(s.GetAll()["additionalGames"])["777"]))
	if entry["source"] != "sgp" || client.Number(entry["gameId"]) != 777 {
		t.Fatal("encounter summary wrapper missing", entry)
	}
	before := len(b.calls)
	if err := s.ReloadPlayerWithOptions(context.Background(), "teammate", map[string]any{"includes": []any{"savedInfo"}}); err != nil {
		t.Fatal(err)
	}
	if len(b.calls) != before {
		t.Fatal("saved-info-only reload queried remote history")
	}
	if err := s.ReloadPlayerWithOptions(context.Background(), "teammate", map[string]any{"includes": []any{}, "excludes": []any{}}); err == nil {
		t.Fatal("conflicting scopes accepted")
	}
	s.Refresh(context.Background())
	if len(b.calls) != before {
		t.Fatal("stable encounter summary fetched again")
	}
}

func TestGsmArenaRosterMapsPartyPositionsAndSpellsOnce(t *testing.T) {
	b := configurable()
	s := New(b, nil)
	client.Map(b.state["champSelect"])["session"] = nil
	client.Map(b.state["gameflow"])["phase"] = "InProgress"
	b.gsm = map[string]any{"gameMode": "CHERRY", "teamOne": []any{map[string]any{"puuid": "self", "championId": 103, "teamParticipantId": 0, "selectedPosition": "MIDDLE", "selectedRole": "MIDDLE.PRIMARY.MIDDLE.TOP"}, map[string]any{"puuid": "teammate", "championId": 147, "teamParticipantId": 0}}, "teamTwo": []any{map[string]any{"puuid": "enemy-one", "teamParticipantId": 1}, map[string]any{"puuid": "enemy-two", "teamParticipantId": 1}}, "playerChampionSelections": []any{map[string]any{"puuid": "self", "spell1Id": 4, "spell2Id": 14}}}
	s.Refresh(context.Background())
	s.Refresh(context.Background())
	state := s.State()
	if len(client.List(client.Map(state["teams"])["TEAM-ALL"])) != 4 || len(client.Map(state["teams"])) != 1 {
		t.Fatal("arena roster not merged", state["teams"])
	}
	if len(client.List(client.Map(state["teamParticipantGroups"])["0"])) != 2 {
		t.Fatal("arena party zero lost")
	}
	role := client.Map(client.Map(client.Map(state["positionAssignments"])["self"])["role"])
	if role["current"] != "MIDDLE" || role["secondary"] != "TOP" {
		t.Fatal("role missing", role)
	}
	if client.Number(client.Map(client.Map(client.Map(state["additional"])["spells"])["self"])["spell1Id"]) != 4 {
		t.Fatal("spells missing")
	}
	count := 0
	for _, path := range b.calls {
		if strings.Contains(path, "/gsm/") {
			count++
		}
	}
	if count != 1 {
		t.Fatal("same-game GSM roster was refetched", count)
	}
}

func TestTaggedPlayerReminderUsesCelebrationOnceAndNeverDuringDraft(t *testing.T) {
	b := configurable()
	p := playerStore(t)
	s := New(b, nil)
	s.SetPlayerStore(p)
	defer s.Close()
	_, err := p.Call("updatePlayerTag", []any{map[string]any{"puuid": "teammate", "selfPuuid": "self", "region": "TENCENT", "rsoPlatformId": "NJ100", "tag": "曾经配合很好"}})
	if err != nil {
		t.Fatal(err)
	}
	s.Refresh(context.Background())
	s.Refresh(context.Background())
	if len(b.chatMessages) != 1 || b.chatMessages[0]["type"] != "celebration" || !strings.Contains(client.String(b.chatMessages[0]["body"]), "曾经配合很好") {
		t.Fatal("local reminder contract", b.chatMessages)
	}
	draft := map[string]any{"gameModeKind": "normal", "queueId": 0, "teams": map[string]any{"TEAM-100": []any{"self", "teammate"}}}
	if err := s.SetDraft(draft); err != nil {
		t.Fatal(err)
	}
	s.Refresh(context.Background())
	if len(b.chatMessages) != 1 {
		t.Fatal("historical simulation sent a reminder")
	}
	if client.Map(s.State()["queryStage"])["phase"] != "draft" {
		t.Fatal("custom game draft queue zero rejected")
	}
}

func TestQueueTagPreferenceTracksQueueKeepsManualParametersAndAllowsAll(t *testing.T) {
	b := configurable()
	store := gameSettings(t)
	s := New(b, nil)
	s.SetSettings(store)
	s.Refresh(context.Background())
	if client.Map(s.State()["matchHistoryTagParams"])["tag"] != "q_2400" {
		t.Fatal("current queue tag missing")
	}
	s.SetMatchHistoryTagParams(map[string]any{"tag": "q_450", "tagsQueryType": "or"})
	s.Refresh(context.Background())
	if client.Map(s.State()["matchHistoryTagParams"])["tag"] != "q_450" {
		t.Fatal("manual filter overwritten without queue change")
	}
	setGameSetting(t, store, "matchHistoryTagPreference", "all")
	s.Refresh(context.Background())
	params := client.Map(s.State()["matchHistoryTagParams"])
	if params["tag"] != nil || params["tagsQueryType"] != "or" {
		t.Fatal("all preference did not merge parameters", params)
	}
	setGameSetting(t, store, "matchHistoryTagPreference", "current")
	client.Map(client.Map(client.Map(client.Map(b.state["gameflow"])["session"])["gameData"])["queue"])["id"] = float64(450)
	s.Refresh(context.Background())
	if client.Map(s.State()["matchHistoryTagParams"])["tag"] != "q_450" {
		t.Fatal("queue change did not refresh tag")
	}
	found := false
	for _, call := range b.calls {
		if strings.Contains(call, "tag=q_450") && strings.Contains(call, "tagsQueryType=or") {
			found = true
		}
	}
	if !found {
		t.Fatal("query did not consume selected tag", b.calls)
	}
}
