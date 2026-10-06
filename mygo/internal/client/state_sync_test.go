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

func TestPollSynchronizesChatProfileAndQueueBeforeSummonerLoads(t *testing.T) {
	var queued atomic.Bool
	queued.Store(true)
	var offline atomic.Bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			t.Errorf("state synchronization wrote to the client: %s %s", r.Method, r.URL.Path)
		}
		if offline.Load() {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/lol-login/v1/login-queue-state":
			if queued.Load() {
				io.WriteString(w, `{"approximateWaitTimeSeconds":120,"estimatedPositionInQueue":7}`)
			} else {
				http.NotFound(w, r)
			}
		case "/lol-summoner/v1/current-summoner":
			if queued.Load() {
				http.NotFound(w, r)
			} else {
				io.WriteString(w, `{"summonerId":10,"puuid":"fixture-self"}`)
			}
		case "/lol-gameflow/v1/gameflow-phase":
			io.WriteString(w, `"None"`)
		case "/lol-summoner/v1/current-summoner/summoner-profile":
			io.WriteString(w, `{"backgroundSkinId":103001,"backgroundSkinAugments":"","regalia":""}`)
		case "/lol-chat/v1/me":
			io.WriteString(w, `{"summonerId":10,"availability":"away"}`)
		case "/lol-chat/v1/conversations":
			io.WriteString(w, `[{"id":"fixture-lol-champ-select@chat","type":"championSelect"},{"id":"fixture-custom@chat","type":"customGame"},{"id":"fixture-post@chat","type":"postGame"},{"id":"friend-chat","type":"chat"}]`)
		case "/lol-chat/v1/conversations/fixture-lol-champ-select@chat/participants":
			io.WriteString(w, `[{"summonerId":10},{"summonerId":11},{"summonerId":11}]`)
		case "/lol-chat/v1/conversations/fixture-custom@chat/participants":
			io.WriteString(w, `[{"summonerId":12}]`)
		case "/lol-chat/v1/conversations/fixture-post@chat/participants":
			io.WriteString(w, `[{"summonerId":13}]`)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	c := NewWithOptions(nil, Options{HTTPClient: server.Client()})
	c.SetAuth(&Auth{BaseURL: server.URL, Region: "TENCENT", PlatformID: "HN1", Password: "fixture"})
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal("login queue prematurely disconnected LCU:", err)
	}
	state := c.State()
	if Map(state["state"])["connectionState"] != "connected" || Map(state["summoner"])["me"] != nil {
		t.Fatal("an available LCU must remain connected before the summoner is ready")
	}
	if Number(Map(Map(state["login"])["loginQueueState"])["estimatedPositionInQueue"]) != 7 {
		t.Fatal("queued player's position is missing")
	}
	queued.Store(false)
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	state = c.State()
	chat := Map(state["chat"])
	if String(Map(chat["me"])["availability"]) != "away" {
		t.Fatal("chat availability was not loaded")
	}
	if String(Map(Map(chat["conversations"])["championSelect"])["id"]) != "fixture-lol-champ-select@chat" {
		t.Fatal("champion-select chat room was not loaded")
	}
	if !reflect.DeepEqual(Map(chat["participants"])["championSelect"], []any{int64(10), int64(11)}) || !reflect.DeepEqual(Map(chat["participants"])["customGame"], []any{int64(12)}) || !reflect.DeepEqual(Map(chat["participants"])["postGame"], []any{int64(13)}) {
		t.Fatal("chat participants must keep the renderer's summoner-id array shape")
	}
	if Number(Map(Map(state["summoner"])["profile"])["backgroundSkinId"]) != 103001 || Map(state["login"])["loginQueueState"] != nil {
		t.Fatal("profile background or completed login-queue state was not synchronized")
	}
	offline.Store(true)
	if err := c.PollOnce(context.Background()); err == nil {
		t.Fatal("invalid LCU credentials did not disconnect")
	}
	state = c.State()
	if Map(state["state"])["connectionState"] != "disconnected" || Map(state["chat"])["me"] != nil || Map(state["summoner"])["profile"] != nil {
		t.Fatal("disconnection retained the previous player's state")
	}
	for _, kind := range chatRoomTypes {
		if Map(Map(state["chat"])["conversations"])[kind] != nil || Map(Map(state["chat"])["participants"])[kind] != nil {
			t.Fatal("disconnection retained chat room or participant state")
		}
	}
}

func TestLoginBeforeQueueEndpointExistsRemainsConnected(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/lol-login/v1/session" {
			io.WriteString(w, `{"state":"IN_PROGRESS","platformId":"HN1"}`)
			return
		}
		http.NotFound(w, r)
	}))
	defer server.Close()
	c := NewWithOptions(nil, Options{HTTPClient: server.Client()})
	c.SetAuth(&Auth{BaseURL: server.URL, Password: "fixture", Region: "TENCENT", PlatformID: "HN1"})
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	if Map(c.State()["state"])["connectionState"] != "connected" {
		t.Fatal("login initialization was mistaken for a missing client")
	}
}

func TestChatEventsReplaceRoomObjectsAndHandleParticipantChanges(t *testing.T) {
	var conversationUpdates []map[string]any
	var participantUpdates []map[string]any
	c := New(func(namespace, name string, args ...any) {
		if name != "update-state-prop/league-client-main:chat" || len(args) < 3 {
			return
		}
		if Map(args[2])["raw"] != true {
			t.Fatal("chat nested state must be sent as a complete raw object")
		}
		switch args[0] {
		case "conversations":
			conversationUpdates = append(conversationUpdates, Map(args[1]))
		case "participants":
			participantUpdates = append(participantUpdates, Map(args[1]))
		}
	})
	dispatch := func(uri, kind string, data any) {
		c.dispatchEvent(map[string]any{"uri": uri, "eventType": kind, "data": data})
	}
	for _, kind := range chatRoomTypes {
		id := "fixture-lol-champ-select-" + kind + "@chat"
		base := "/lol-chat/v1/conversations/" + id
		dispatch(base, "Create", map[string]any{"id": id, "type": kind, "name": "room"})
		dispatch(base+"/participants", "Update", []any{map[string]any{"summonerId": 10}, map[string]any{"summonerId": 11}})
		dispatch(base+"/participants/11", "Delete", nil)
		dispatch(base+"/messages/1", "Create", map[string]any{"type": "system", "body": "joined_room", "fromSummonerId": 12})
		dispatch(base, "Update", map[string]any{"id": id, "type": kind, "name": "updated"})
		chat := Map(c.State()["chat"])
		if String(Map(Map(chat["conversations"])[kind])["name"]) != "updated" || !reflect.DeepEqual(Map(chat["participants"])[kind], []any{int64(10), int64(12)}) {
			t.Fatal("room update or joined/left participants were not reflected")
		}
		dispatch(base, "Delete", map[string]any{"id": id, "type": kind})
		chat = Map(c.State()["chat"])
		if Map(chat["conversations"])[kind] != nil || Map(chat["participants"])[kind] != nil {
			t.Fatal("deleted room kept stale participants")
		}
	}
	if len(conversationUpdates) != 9 || len(participantUpdates) != 15 {
		t.Fatalf("renderer room updates missing: conversations=%d participants=%d", len(conversationUpdates), len(participantUpdates))
	}
	if String(Map(conversationUpdates[0]["championSelect"])["name"]) != "room" || len(List(participantUpdates[1]["championSelect"])) != 2 {
		t.Fatal("later chat changes mutated an earlier renderer snapshot")
	}
	dispatch("/lol-chat/v1/me", "Update", map[string]any{"availability": "chat"})
	if String(Map(Map(c.State()["chat"])["me"])["availability"]) != "chat" {
		t.Fatal("chat presence event was not synchronized")
	}
	dispatch("/lol-chat/v1/me", "Delete", map[string]any{"availability": "chat"})
	if Map(c.State()["chat"])["me"] != nil {
		t.Fatal("deleted chat identity was retained")
	}
}

func TestRealtimeSelectionProfileAndQueueEventsClearOnExit(t *testing.T) {
	c := New(nil)
	dispatch := func(uri, kind string, data any) {
		c.dispatchEvent(map[string]any{"uri": uri, "eventType": kind, "data": data})
	}
	dispatch("/lol-champ-select/v1/session", "Update", map[string]any{"localPlayerCellId": 1, "myTeam": []any{map[string]any{"cellId": 0, "championId": 103}, map[string]any{"cellId": 1, "championId": 23}}})
	if Number(Map(c.State()["champSelect"])["currentChampion"]) != 23 {
		t.Fatal("session event did not immediately update the local player's selected champion")
	}
	dispatch("/lol-champ-select/v1/current-champion", "Update", 421)
	if Number(Map(c.State()["champSelect"])["currentChampion"]) != 421 {
		t.Fatal("current-champion event did not update selection")
	}
	for _, list := range []struct{ endpoint, key string }{{"pickable-champion-ids", "currentPickableChampionIds"}, {"bannable-champion-ids", "currentBannableChampionIds"}, {"disabled-champion-ids", "disabledChampionIds"}} {
		dispatch("/lol-champ-select/v1/"+list.endpoint, "Update", []any{23, 421})
		if len(List(Map(c.State()["champSelect"])[list.key])) != 2 {
			t.Fatal("realtime champion eligibility missing:", list.key)
		}
		dispatch("/lol-champ-select/v1/"+list.endpoint, "Delete", []any{23, 421})
		if list, ok := Map(c.State()["champSelect"])[list.key].([]any); !ok || len(list) != 0 {
			t.Fatal("deleted eligibility must retain an empty array")
		}
	}
	dispatch("/lol-champ-select/v1/skin-selector-info", "Update", map[string]any{"selectedSkinId": 421001})
	dispatch("/lol-champ-select/v1/ongoing-champion-swap", "Update", map[string]any{"championId": 23})
	dispatch("/lol-gameflow/v1/gameflow-phase", "Update", "InProgress")
	selection := Map(c.State()["champSelect"])
	if selection["session"] != nil || selection["currentChampion"] != nil || selection["skinSelectorInfo"] != nil || selection["ongoingChampionSwap"] != nil {
		t.Fatal("leaving champion selection retained stale hero/skin state")
	}
	for _, endpoint := range []struct{ path, state, key string }{{"/lol-login/v1/login-queue-state", "login", "loginQueueState"}, {"/lol-summoner/v1/current-summoner/summoner-profile", "summoner", "profile"}} {
		dispatch(endpoint.path, "Update", map[string]any{"fixture": true})
		if Map(c.State()[endpoint.state])[endpoint.key] == nil {
			t.Fatal("profile or queue event was not synchronized")
		}
		dispatch(endpoint.path, "Delete", map[string]any{"fixture": true})
		if Map(c.State()[endpoint.state])[endpoint.key] != nil {
			t.Fatal("profile or queue deletion was not synchronized")
		}
	}
	dispatch("/lol-login/v1/login-queue-state", "Create", map[string]any{"estimatedPositionInQueue": 1})
	c.Disconnect()
	dispatch("/lol-login/v1/login-queue-state", "Update", map[string]any{"estimatedPositionInQueue": 9})
	if Map(c.State()["login"])["loginQueueState"] != nil {
		t.Fatal("disconnect retained or repopulated the previous login queue")
	}
}
