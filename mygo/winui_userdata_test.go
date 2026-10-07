package main

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

func isolatedConfigFixture(t *testing.T) (string, string) {
	t.Helper()
	base := t.TempDir()
	t.Setenv("APPDATA", base)
	t.Setenv("XDG_CONFIG_HOME", base)
	legacyPath := filepath.Join(base, "Timo", "LeagueAkari.db")
	if err := os.MkdirAll(filepath.Dir(legacyPath), 0700); err != nil {
		t.Fatal(err)
	}
	db, err := sqlite.Open(legacyPath, false)
	if err != nil {
		t.Fatal(err)
	}
	for _, sql := range []string{
		`CREATE TABLE SavedPlayers(puuid TEXT,selfPuuid TEXT,region TEXT,rsoPlatformId TEXT,tag TEXT,updateAt TEXT,lastMetAt TEXT)`,
		`CREATE TABLE EncounteredGames(id INTEGER PRIMARY KEY,gameId INTEGER,puuid TEXT,selfPuuid TEXT,region TEXT,rsoPlatformId TEXT,updateAt TEXT)`,
		`CREATE TABLE Settings(key TEXT PRIMARY KEY,value TEXT)`,
		`INSERT INTO SavedPlayers VALUES('legacy-player','self','TENCENT','HN1','external tag','2026-05-16 10:20:30.000',NULL)`,
		`INSERT INTO EncounteredGames VALUES(1,123,'legacy-player','self','TENCENT','HN1','2026-05-16 10:20:30.000')`,
		`INSERT INTO Settings VALUES('auto-gameflow-main/autoAcceptEnabled','true')`,
	} {
		if _, err := db.Exec(sql); err != nil {
			db.Close()
			t.Fatal(err)
		}
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	return base, legacyPath
}

func profileDesktopFixture(t *testing.T, directory string, explicit bool) *Desktop {
	t.Helper()
	store, err := settings.New(filepath.Join(directory, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	store.ApplyDefaults(map[string]map[string]any{"auto-gameflow-main": {"autoAcceptEnabled": false}})
	players, err := player.New(filepath.Join(directory, "players.sqlite"), store, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { players.Close() })
	return &Desktop{store: store, player: players, userData: directory, skipExternalStorageMigration: explicit}
}

func profilePlayer(t *testing.T, d *Desktop, method, puuid string, extra object) any {
	t.Helper()
	dto := object{"puuid": puuid, "selfPuuid": "self", "region": "TENCENT", "rsoPlatformId": "HN1"}
	for key, value := range extra {
		dto[key] = value
	}
	result, err := d.player.Call(method, []any{dto})
	if err != nil {
		t.Fatal(err)
	}
	return result
}

func TestWinUIExplicitProfileStartupDoesNotImportExternalSettingsOrPlayers(t *testing.T) {
	base, legacy := isolatedConfigFixture(t)
	before, err := os.ReadFile(legacy)
	if err != nil {
		t.Fatal(err)
	}
	directory := filepath.Join(base, "isolated", "profile")
	dir, explicit, err := resolveWinUIUserData([]string{"--winui-backend", "league-akari-winui-test", "--host-pid", "1", "--user-data", directory})
	if err != nil || !explicit || dir != directory {
		t.Fatal(dir, explicit, err)
	}
	d := profileDesktopFixture(t, dir, explicit)
	d.migrateStorage() // The same migration invoked by Desktop.run; no client/poll/network is started.
	if d.store.Get("auto-gameflow-main", "autoAcceptEnabled") != false {
		t.Fatal("external auto-accept was imported into isolated startup")
	}
	if profilePlayer(t, d, "querySavedPlayer", "legacy-player", nil) != nil {
		t.Fatal("external player imported")
	}
	encounters := asObject(profilePlayer(t, d, "queryEncounteredGames", "legacy-player", nil))
	if encounters["total"] != int64(0) {
		t.Fatalf("external encounters imported: %#v", encounters)
	}
	after, err := os.ReadFile(legacy)
	if err != nil || !bytes.Equal(before, after) {
		t.Fatal("external source modified", err)
	}
	if _, err = os.Stat(filepath.Join(base, userDataDirectory)); !os.IsNotExist(err) {
		t.Fatal("explicit directory still prepared shared profile")
	}
}

func TestWinUIExplicitProfileRetainsExistingLocalConfigurationAndRecords(t *testing.T) {
	base, _ := isolatedConfigFixture(t)
	dir, explicit, err := resolveWinUIUserData([]string{"--winui-backend", "league-akari-winui-test", "--user-data", filepath.Join(base, "existing-profile")})
	if err != nil {
		t.Fatal(err)
	}
	d := profileDesktopFixture(t, dir, explicit)
	if err = d.store.Set("auto-gameflow-main", "autoAcceptEnabled", true); err != nil {
		t.Fatal(err)
	}
	profilePlayer(t, d, "updatePlayerTag", "local-player", object{"tag": "local tag"})
	d.migrateStorage()
	if d.store.Get("auto-gameflow-main", "autoAcceptEnabled") != true || asObject(profilePlayer(t, d, "querySavedPlayer", "local-player", nil))["tag"] != "local tag" {
		t.Fatal("profile's own settings or players changed")
	}
	reopened, err := settings.New(filepath.Join(dir, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	if reopened.Get("auto-gameflow-main", "autoAcceptEnabled") != true {
		t.Fatal("local preference not preserved on disk")
	}
	if profilePlayer(t, d, "querySavedPlayer", "legacy-player", nil) != nil {
		t.Fatal("existing explicit profile imported external record")
	}
}

func TestWinUIDefaultProfileStartupStillMigratesLegacyStorage(t *testing.T) {
	base, _ := isolatedConfigFixture(t)
	dir, explicit, err := resolveWinUIUserData([]string{"--winui-backend", "league-akari-winui-test"})
	if err != nil || explicit || dir != filepath.Join(base, userDataDirectory) {
		t.Fatal(dir, explicit, err)
	}
	d := profileDesktopFixture(t, dir, explicit)
	d.migrateStorage()
	if d.store.Get("auto-gameflow-main", "autoAcceptEnabled") != true || asObject(profilePlayer(t, d, "querySavedPlayer", "legacy-player", nil))["tag"] != "external tag" {
		t.Fatal("default startup migration suppressed")
	}
}

func TestWinUIExplicitProfileAllowsManualLegacyMigration(t *testing.T) {
	base, legacy := isolatedConfigFixture(t)
	d := profileDesktopFixture(t, filepath.Join(base, "manual-profile"), true)
	d.migrateStorage()
	report, err := d.player.Migration([]string{legacy})
	if err != nil || report.Files != 1 || report.Players != 1 || report.Encounters != 1 || report.Settings != 1 {
		t.Fatal(report, err)
	}
	if d.store.Get("auto-gameflow-main", "autoAcceptEnabled") != true {
		t.Fatal("explicit manual migration no longer imports chosen settings")
	}
}

func TestWinUIExplicitProfileUpgradesOnlyItsOwnEarlyPlayerRecords(t *testing.T) {
	base, _ := isolatedConfigFixture(t)
	d := profileDesktopFixture(t, filepath.Join(base, "early-profile"), true)
	if err := d.store.Set("saved-player-data", "self:early-player", object{"puuid": "early-player", "selfPuuid": "self", "region": "TENCENT", "rsoPlatformId": "HN1", "tag": "same profile"}); err != nil {
		t.Fatal(err)
	}
	d.migrateStorage()
	if asObject(profilePlayer(t, d, "querySavedPlayer", "early-player", nil))["tag"] != "same profile" {
		t.Fatal("isolated startup stopped upgrading records already in its own settings")
	}
	if d.store.Get("auto-gameflow-main", "autoAcceptEnabled") != false || profilePlayer(t, d, "querySavedPlayer", "legacy-player", nil) != nil {
		t.Fatal("local upgrade also imported external legacy profile")
	}
}

func TestWinUIEmptyExplicitProfileDoesNotFallBackToSharedDirectory(t *testing.T) {
	for _, args := range [][]string{{"--winui-backend", "league-akari-winui-test", "--user-data"}, {"--winui-backend", "league-akari-winui-test", "--user-data", " "}} {
		if _, explicit, err := resolveWinUIUserData(args); err == nil || !explicit {
			t.Fatal("missing explicit directory fell back to user's shared profile")
		}
	}
}
