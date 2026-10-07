package main

import (
	"context"
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	mini "github.com/zhengchalei/LeagueAkari-MyGo/mygo/native_mini"
)

func TestWinUIBackendRequestsUseInternalAuthAndPreserveJSON(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		user, password, ok := r.BasicAuth()
		if !ok || user != "riot" || password != "private-token" {
			t.Error("backend did not retain its LCU authentication")
			w.WriteHeader(401)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"gameName":"local-player","summonerId":99}`))
	}))
	defer server.Close()
	c := client.New(nil)
	c.SetAuth(&client.Auth{BaseURL: server.URL, Password: "private-token"})
	d := &Desktop{client: c}
	value, err := d.winUIBackendCall(context.Background(), nil, "lcuRequest", []any{"GET", "/lol-summoner/v1/current-summoner"})
	if err != nil {
		t.Fatal(err)
	}
	if asObject(value)["gameName"] != "local-player" {
		t.Fatalf("incorrect response: %v", value)
	}
	value, err = d.winUIBackendCall(context.Background(), nil, "image", []any{"/lol-game-data/assets/v1/champion-icons/1.png"})
	if err != nil {
		t.Fatal(err)
	}
	data, err := base64.StdEncoding.DecodeString(asObject(value)["base64"].(string))
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != `{"gameName":"local-player","summonerId":99}` {
		t.Fatalf("asset data corrupted: %s", data)
	}
	if _, err = d.winUIBackendCall(context.Background(), nil, "lcuRequest", []any{"GET", "https://external.example/steal"}); err == nil {
		t.Fatal("absolute LCU URL accepted")
	}
}
func TestWinUIHeadlessSettingsExportUsesNativeHostPicker(t *testing.T) {
	directory := t.TempDir()
	store, err := settings.New(filepath.Join(directory, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(directory, "export.json")
	d := &Desktop{store: store, hostCall: func(ctx context.Context, namespace, method string, args []any) (any, error) {
		if namespace != "host-ui" || method != "fileDialog" || args[0] != "save" {
			t.Fatalf("unexpected native picker request %s %s %v", namespace, method, args)
		}
		return target, nil
	}}
	value, handled, err := d.headlessCall(context.Background(), "setting-factory-main", "exportSettingsToJsonFile", nil)
	if err != nil || !handled || value != target {
		t.Fatalf("export failed: %v %t %v", value, handled, err)
	}
	if data, err := os.ReadFile(target); err != nil || len(data) == 0 {
		t.Fatalf("export not saved: %v", err)
	}
}
func TestWinUIRiotAccountRoutesUseNationalClientAdapter(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/lol-summoner/v1/summoners/aliases" {
			w.Write([]byte(`[{"puuid":"player-puuid","gameName":"真实别名","tagLine":"123"}]`))
			return
		}
		w.Write([]byte(`{"puuid":"player-puuid","gameName":"真实别名","tagLine":"123"}`))
	}))
	defer server.Close()
	c := client.New(nil)
	c.SetAuth(&client.Auth{BaseURL: server.URL, Password: "fixture-token"})
	d := &Desktop{client: c}
	value, err := d.winUIBackendCall(context.Background(), nil, "riotRequest", []any{"GET", "/player-account/aliases/v1/lookup?gameName=name&tagLine=123", nil})
	if err != nil || len(client.List(value)) != 1 || asObject(client.List(value)[0])["puuid"] != "player-puuid" {
		t.Fatalf("alias adapter: %v %v", value, err)
	}
	value, err = d.winUIBackendCall(context.Background(), nil, "riotRequest", []any{"POST", "/player-account/lookup/v1/namesets-for-puuids", object{"puuids": []string{"player-puuid"}}})
	if err != nil || len(client.List(asObject(value)["namesets"])) != 1 {
		t.Fatalf("nameset adapter: %v %v", value, err)
	}
	if _, err = d.winUIBackendCall(context.Background(), nil, "riotRequest", []any{"GET", "https://outside/player-account/foo", nil}); err == nil {
		t.Fatal("external player account URL accepted")
	}
}
func TestWinUIMiniSettingsActionsRetainFractionalDelays(t *testing.T) {
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	d := &Desktop{store: store}
	if _, err = d.winUIBackendCall(context.Background(), mini.New(nil), "miniAction", []any{object{"kind": "set-auto-delay", "id": 2500}}); err != nil {
		t.Fatal(err)
	}
	if store.Get("auto-gameflow-main", "autoMatchmakingDelaySeconds") != 2.5 {
		t.Fatal("fractional delay lost")
	}
	if _, err = d.winUIBackendCall(context.Background(), nil, "miniAction", []any{object{"kind": "set-auto-min-members", "id": 100}}); err == nil {
		t.Fatal("invalid party count accepted")
	}
}

func TestWinUIMiniSelectionActionsRevalidateLatestClientState(t *testing.T) {
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	d := &Desktop{store: store, client: client.New(nil)} // Disconnected client; no real discovery or LCU requests.
	for _, kind := range []string{"champion", "champion-preview", "reroll", "reroll-grab-back", "skin"} {
		t.Run(kind, func(t *testing.T) {
			writes := 0
			model := mini.New(func(_ context.Context, method, path string, _ any) (any, error) {
				if method != "GET" {
					writes++
				}
				return nil, nil
			})
			model.Refresh(context.Background(), mini.Inputs{Client: map[string]any{
				"state":       map[string]any{"connectionState": "connected"},
				"gameflow":    map[string]any{"phase": "ChampSelect"},
				"champSelect": map[string]any{"currentChampion": 23, "currentPickableChampionIds": []any{421}, "session": map[string]any{"benchEnabled": true, "allowRerolling": true, "rerollsRemaining": 1, "timer": map[string]any{"phase": "FINALIZATION"}, "benchChampions": []any{map[string]any{"championId": 421}}}},
			}})
			if _, err := d.winUIBackendCall(context.Background(), model, "miniAction", []any{object{"kind": kind, "id": 421}}); err == nil {
				t.Fatal("stale selection accepted")
			}
			if writes != 0 || model.Snapshot().Connected {
				t.Fatal("rendered old selection wrote after disconnect")
			}
		})
	}
}

func TestWinUIMiniStateExcludesUnrelatedAssetCatalogs(t *testing.T) {
	source := map[string]any{"gameData": map[string]any{"champions": map[string]any{"147": map[string]any{"name": "Seraphine"}}, "items": map[string]any{"1": "large catalog"}}, "champSelect": map[string]any{"session": map[string]any{"localPlayerCellId": 3}}, "matchmaking": map[string]any{"readyCheck": map[string]any{"playerResponse": "None"}}, "state": map[string]any{"auth": "backend only"}}
	result := winUIMiniState(source)
	if client.Map(client.Map(result["gameData"])["champions"])["147"] == nil {
		t.Fatal("champion names removed")
	}
	if _, present := client.Map(result["gameData"])["items"]; present {
		t.Fatal("unrelated full asset catalog included")
	}
	if result["champSelect"] == nil || result["matchmaking"] == nil {
		t.Fatal("live interaction state removed")
	}
	if _, present := result["state"]; present {
		t.Fatal("connection credentials included in Mini state")
	}
}
