package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/game"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type importedHistoryBackend struct{ requests []string }

func (*importedHistoryBackend) State() map[string]any { return map[string]any{} }
func (*importedHistoryBackend) CurrentServer() string { return "TENCENT_NJ100" }
func (*importedHistoryBackend) JSON(context.Context, string, string, any) (any, error) {
	return map[string]any{}, nil
}
func (b *importedHistoryBackend) SGPJSON(_ context.Context, _, _, _, path string, _ any) (any, error) {
	b.requests = append(b.requests, path)
	return map[string]any{"games": []any{}}, nil
}

func TestImportedSettingsApplyRunningClientAndHistoryConfiguration(t *testing.T) {
	directory := t.TempDir()
	store, err := settings.New(filepath.Join(directory, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	store.ApplyDefaults(map[string]map[string]any{
		"league-client-main": {"autoConnect": false},
		"ongoing-game-main":  {"matchHistoryLoadCount": 2},
	})
	discoveries := 0
	lc := client.NewWithOptions(nil, client.Options{Discover: func(context.Context) (*client.Auth, error) {
		discoveries++
		return nil, nil
	}})
	lc.SetAutoConnect(false)
	backend := &importedHistoryBackend{}
	ongoing := game.New(backend, nil)
	ongoing.SetMatchHistoryLoadCount(2)
	d := &Desktop{store: store, client: lc, game: ongoing, static: staticStates()}
	store.OnChange(d.settingChanged)
	ctx := context.Background()
	if err := lc.PollOnce(ctx); err != nil || discoveries != 0 {
		t.Fatalf("disabled client tried discovery: %v", err)
	}
	options := map[string]any{"includes": []any{"matchHistory"}}
	if err := ongoing.ReloadPlayerWithOptions(ctx, "fixture-player", options); err != nil {
		t.Fatal(err)
	}
	if len(backend.requests) != 1 || !strings.Contains(backend.requests[0], "count=2") {
		t.Fatal("initial history count not applied", backend.requests)
	}
	imported, err := json.Marshal(object{"version": 1, "namespaces": object{
		"league-client-main": object{"autoConnect": true},
		"ongoing-game-main":  object{"matchHistoryLoadCount": 7},
		"app-common-main":    object{"disableHardwareAcceleration": true},
	}})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(directory, "import.json")
	if err := os.WriteFile(path, imported, 0600); err != nil {
		t.Fatal(err)
	}
	if err := store.Import(path); err != nil {
		t.Fatal(err)
	}
	if err := lc.PollOnce(ctx); err != nil || discoveries != 1 {
		t.Fatalf("imported autoConnect did not enable discovery: discoveries=%d error=%v", discoveries, err)
	}
	if client.Map(lc.State()["settings"])["autoConnect"] != true {
		t.Fatal("client setting still differs from imported switch")
	}
	if err := ongoing.ReloadPlayerWithOptions(ctx, "fixture-player", options); err != nil {
		t.Fatal(err)
	}
	if len(backend.requests) != 2 || !strings.Contains(backend.requests[1], "count=7") {
		t.Fatal("imported history count did not reach loader", backend.requests)
	}
	if asObject(d.state("app-common-main", "state")["baseConfig"])["disableHardwareAcceleration"] != true {
		t.Fatal("imported restart setting not synchronized")
	}
}
