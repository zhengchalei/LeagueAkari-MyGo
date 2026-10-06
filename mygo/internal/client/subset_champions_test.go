package client

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"sync/atomic"
	"testing"
)

func assertSubsetChampions(t *testing.T, c *Client, expected ...int64) {
	t.Helper()
	champSelect := Map(Map(c.State()["lobbyTeamBuilder"])["champSelect"])
	list, ok := champSelect["subsetChampionList"].([]any)
	if !ok {
		t.Fatal("candidate list must remain an array, including after deletion")
	}
	actual := make([]int64, len(list))
	for index, value := range list {
		actual[index] = Number(value)
	}
	if !reflect.DeepEqual(actual, append([]int64{}, expected...)) {
		t.Fatalf("candidate champions = %v, want %v", actual, expected)
	}
}

func TestPollLoadsPersonalSubsetInsteadOfBenchOrPickableChampions(t *testing.T) {
	var selecting atomic.Bool
	selecting.Store(true)
	var subsetRequests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			t.Errorf("poll unexpectedly changed client state: %s %s", r.Method, r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/lol-summoner/v1/current-summoner":
			io.WriteString(w, `{"puuid":"fixture-self"}`)
		case "/lol-gameflow/v1/gameflow-phase":
			if selecting.Load() {
				io.WriteString(w, `"ChampSelect"`)
			} else {
				io.WriteString(w, `"InProgress"`)
			}
		case "/lol-champ-select/v1/session":
			if !selecting.Load() {
				http.NotFound(w, r)
				return
			}
			io.WriteString(w, `{"localPlayerCellId":0,"myTeam":[{"cellId":0,"championId":0}],"benchEnabled":true,"allowSubsetChampionPicks":true,"benchChampions":[{"championId":67},{"championId":143}],"timer":{"phase":"BAN_PICK"}}`)
		case subsetChampionListEndpoint:
			subsetRequests.Add(1)
			io.WriteString(w, `[23,421,202]`)
		case "/lol-champ-select/v1/pickable-champion-ids":
			io.WriteString(w, `[23,421,202,67,143,103]`)
		default:
			io.WriteString(w, `[]`)
		}
	}))
	defer server.Close()
	c := NewWithOptions(nil, Options{HTTPClient: server.Client()})
	c.SetAuth(&Auth{BaseURL: server.URL, Password: "test-only", Region: "TENCENT", PlatformID: "HN1"})
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	assertSubsetChampions(t, c, 23, 421, 202)
	if Number(Map(c.State()["champSelect"])["currentChampion"]) != 0 {
		t.Fatal("loading personal candidates must not select a champion")
	}
	selecting.Store(false)
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	assertSubsetChampions(t, c)
	if subsetRequests.Load() != 1 {
		t.Fatal("subset endpoint should only be polled during champion selection")
	}
}

func TestSubsetEventsReplaceNestedStateForBothWindows(t *testing.T) {
	var updates []map[string]any
	c := New(func(namespace, name string, args ...any) {
		if namespace == "mobx-utils-main" && name == "update-state-prop/league-client-main:lobbyTeamBuilder" {
			if args[0] != "champSelect" || Map(args[2])["raw"] != true {
				t.Fatal("candidate update must replace the renderer's raw nested object")
			}
			updates = append(updates, Map(args[1]))
		}
	})
	for _, eventType := range []string{"Create", "Update"} {
		c.dispatchEvent(map[string]any{"uri": subsetChampionListEndpoint, "eventType": eventType, "data": []any{23, 421, 202}})
		assertSubsetChampions(t, c, 23, 421, 202)
		c.dispatchEvent(map[string]any{"uri": subsetChampionListEndpoint, "eventType": "Delete", "data": []any{23, 421, 202}})
		assertSubsetChampions(t, c)
	}
	if len(updates) != 4 || len(List(updates[0]["subsetChampionList"])) != 3 || len(List(updates[1]["subsetChampionList"])) != 0 {
		t.Fatalf("windows did not receive candidate creation and deletion: %v", updates)
	}
}

func TestSubsetClearsWhenSelectionEndsOrClientDisconnects(t *testing.T) {
	for _, reason := range []string{"session deleted", "phase changed", "disconnected"} {
		t.Run(reason, func(t *testing.T) {
			c := New(nil)
			c.dispatchEvent(map[string]any{"uri": subsetChampionListEndpoint, "eventType": "Create", "data": []any{23, 421, 202}})
			switch reason {
			case "session deleted":
				c.dispatchEvent(map[string]any{"uri": "/lol-champ-select/v1/session", "eventType": "Delete"})
			case "phase changed":
				c.dispatchEvent(map[string]any{"uri": "/lol-gameflow/v1/gameflow-phase", "eventType": "Update", "data": "InProgress"})
			case "disconnected":
				c.Disconnect()
			}
			assertSubsetChampions(t, c)
		})
	}
}
