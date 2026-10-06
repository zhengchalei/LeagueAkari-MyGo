package main

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/game"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestFixedTextReachesTheCurrentSelectionAndLobbyRoom(t *testing.T) {
	var phase atomic.Value
	phase.Store("ChampSelect")
	type sentMessage struct {
		Room string
		Body string
		Type string
	}
	sent := make(chan sentMessage, 2)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/messages") {
			var payload struct{ Body, Type string }
			if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
				t.Error(err)
			}
			sent <- sentMessage{strings.TrimSuffix(strings.TrimPrefix(r.URL.Path, "/lol-chat/v1/conversations/"), "/messages"), payload.Body, payload.Type}
			io.WriteString(w, `{}`)
			return
		}
		switch r.URL.Path {
		case "/lol-summoner/v1/current-summoner", "/lol-summoner/v1/current-summoner/summoner-profile":
			io.WriteString(w, `{}`)
		case "/lol-chat/v1/me":
			io.WriteString(w, `{"availability":"chat"}`)
		case "/lol-gameflow/v1/gameflow-phase":
			json.NewEncoder(w).Encode(phase.Load())
		case "/lol-champ-select/v1/session":
			if phase.Load() != "ChampSelect" {
				http.NotFound(w, r)
				return
			}
			io.WriteString(w, `{"myTeam":[],"timer":{"phase":"FINALIZATION"}}`)
		case "/lol-lobby/v2/lobby":
			if phase.Load() == "Lobby" {
				io.WriteString(w, `{"members":[],"gameConfig":{"queueId":450}}`)
			} else {
				http.NotFound(w, r)
			}
		case "/lol-chat/v1/conversations":
			if phase.Load() == "ChampSelect" {
				io.WriteString(w, `[{"id":"fixture-lol-champ-select@chat","type":"championSelect"}]`)
			} else {
				io.WriteString(w, `[{"id":"fixture-custom-game@chat","type":"customGame"}]`)
			}
		default:
			io.WriteString(w, `[]`)
		}
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{BaseURL: server.URL, Region: "TENCENT", PlatformID: "HN1", Password: "fixture-only"})
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("in-game-send-main", "fixedTextPresetItems", []any{object{"id": "fixture", "content": "第一行\n第二行"}}); err != nil {
		t.Fatal(err)
	}
	d := &Desktop{store: store, client: lc, game: game.New(lc, nil)}
	for _, mode := range []struct{ phase, room string }{{"ChampSelect", "fixture-lol-champ-select@chat"}, {"Lobby", "fixture-custom-game@chat"}} {
		phase.Store(mode.phase)
		if err := lc.PollOnce(context.Background()); err != nil {
			t.Fatal(err)
		}
		if err := d.game.Refresh(context.Background()); err != nil {
			t.Fatal(err)
		}
		result := d.Call(context.Background(), "in-game-send-main", "sendFixedTextPreset", []any{"fixture"})
		if !result.Success || result.Data != true {
			t.Fatalf("%s message did not reach current room: %#v", mode.phase, result)
		}
		message := <-sent
		if message.Room != mode.room || message.Body != "第一行\n第二行" || message.Type != "chat" {
			t.Fatalf("message sent to wrong room or changed content: %#v", message)
		}
	}
}
