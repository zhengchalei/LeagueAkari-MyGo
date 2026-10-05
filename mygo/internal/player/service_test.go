package player

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

func testService(t *testing.T) *Service {
	t.Helper()
	dir := t.TempDir()
	store, err := settings.New(filepath.Join(dir, "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	s, err := New(filepath.Join(dir, "players.sqlite"), store, nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}
func call(t *testing.T, s *Service, method string, value any) any {
	t.Helper()
	result, err := s.Call(method, []any{value})
	if err != nil {
		t.Fatal(method, err)
	}
	return result
}
func identity(puuid, self string) map[string]any {
	return map[string]any{"puuid": puuid, "selfPuuid": self, "region": "TENCENT", "rsoPlatformId": "NJ100"}
}

func TestPlayerTagsPersistFilterPaginateAndRemainOnEncounter(t *testing.T) {
	s := testService(t)
	a := identity("target'中文", "self")
	a["tag"] = "队友 / 好配合"
	result := call(t, s, "updatePlayerTag", a).(map[string]any)
	if result["tag"] != a["tag"] {
		t.Fatal(result)
	}
	a["encountered"] = true
	delete(a, "tag")
	call(t, s, "saveSavedPlayer", a)
	result = call(t, s, "querySavedPlayerWithGames", a).(map[string]any)
	if result["tag"] != "队友 / 好配合" || result["lastMetAt"] == nil {
		t.Fatal("encounter erased tag", result)
	}
	b := identity("target'中文", "other")
	b["tag"] = "另一个标签"
	call(t, s, "updatePlayerTag", b)
	tags := call(t, s, "getPlayerTags", a).([]sqlite.Row)
	if len(tags) != 2 {
		t.Fatal(tags)
	}
	marked := 0
	for _, tag := range tags {
		if tag["markedBySelf"] == true {
			marked++
		}
	}
	if marked != 1 {
		t.Fatal(tags)
	}
	page := call(t, s, "getAllPlayerTags", map[string]any{"selfPuuid": "self", "page": 1, "pageSize": 1, "search": "好配合"}).(map[string]any)
	if page["total"] != int64(1) || len(page["data"].([]sqlite.Row)) != 1 {
		t.Fatal(page)
	}
	a["tag"] = nil
	call(t, s, "updatePlayerTag", a)
	if len(call(t, s, "getPlayerTags", a).([]sqlite.Row)) != 1 {
		t.Fatal("remove tag failed")
	}
	call(t, s, "deleteSavedPlayer", b)
	if call(t, s, "querySavedPlayer", b) != nil {
		t.Fatal("delete did not persist")
	}
}

func TestEncountersDeduplicateAcrossRestartAndRespectRegion(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "players.sqlite")
	s, err := New(path, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	dto := Encounter{GameID: 900000000001, Puuid: "other", SelfPuuid: "self", Region: "TENCENT", RsoPlatformID: "NJ100", QueueType: "ARAM_UNRANKED_5x5"}
	first, err := s.RecordEncounter(dto)
	if err != nil {
		t.Fatal(err)
	}
	second, err := s.RecordEncounter(dto)
	if err != nil {
		t.Fatal(err)
	}
	if first.(map[string]any)["id"] != second.(map[string]any)["id"] {
		t.Fatal("duplicate inserted")
	}
	s.Close()
	s, err = New(path, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	s.RecordEncounter(dto)
	dto.GameID++
	s.RecordEncounter(dto)
	dto.RsoPlatformID = "HN1"
	s.RecordEncounter(dto)
	query := map[string]any{"puuid": "other", "selfPuuid": "self", "region": "TENCENT", "rsoPlatformId": "NJ100", "pageSize": 1, "page": 2}
	page := call(t, s, "queryEncounteredGames", query).(map[string]any)
	if page["total"] != int64(2) || len(page["data"].([]sqlite.Row)) != 1 {
		t.Fatal(page)
	}
	call(t, s, "deleteEncounteredGame", page["data"].([]sqlite.Row)[0]["id"])
	if call(t, s, "queryEncounteredGames", query).(map[string]any)["total"] != int64(1) {
		t.Fatal("delete encounter failed")
	}
}

func TestTagFileRoundTripRejectsBadRowsAtomically(t *testing.T) {
	s := testService(t)
	dto := identity("target", "self")
	dto["tag"] = "记住此人"
	call(t, s, "updatePlayerTag", dto)
	path := filepath.Join(t.TempDir(), "tags.json")
	call(t, s, "exportTaggedPlayersToJsonFile", path)
	target := testService(t)
	call(t, target, "importTaggedPlayersFromJsonFile", path)
	if call(t, target, "querySavedPlayer", dto).(map[string]any)["tag"] != "记住此人" {
		t.Fatal("round trip failed")
	}
	bad := tagDocument{DatabaseVersion: 15, Type: "league-akari-tagged-players", Data: []map[string]any{identity("valid", "self"), identity("invalid", "self")}}
	bad.Data[0]["tag"] = "valid"
	bad.Data[1]["tag"] = 99
	raw, _ := json.Marshal(bad)
	os.WriteFile(path, raw, 0600)
	if _, err := target.ImportTags(path); err == nil {
		t.Fatal("invalid file accepted")
	}
	if call(t, target, "querySavedPlayer", identity("valid", "self")) != nil {
		t.Fatal("partial invalid import")
	}
}

func TestLegacyReadOnlyMigrationDeduplicatesAndPreservesCurrent(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "LeagueAkari.db")
	legacy, err := sqlite.Open(path, false)
	if err != nil {
		t.Fatal(err)
	}
	for _, q := range []string{`CREATE TABLE SavedPlayers(puuid TEXT,selfPuuid TEXT,region TEXT,rsoPlatformId TEXT,tag TEXT,updateAt TEXT,lastMetAt TEXT)`, `CREATE TABLE EncounteredGames(id INTEGER PRIMARY KEY,gameId INTEGER,puuid TEXT,selfPuuid TEXT,region TEXT,rsoPlatformId TEXT,updateAt TEXT)`, `CREATE TABLE Settings(key TEXT PRIMARY KEY,value TEXT)`} {
		if _, err := legacy.Exec(q); err != nil {
			t.Fatal(err)
		}
	}
	legacy.Exec(`INSERT INTO SavedPlayers VALUES('target','self','TENCENT','NJ100','old','2026-05-16 10:20:30.000',NULL)`)
	legacy.Exec(`INSERT INTO SavedPlayers VALUES('imported','self','TENCENT','NJ100','中文','2026-05-16 10:20:30.000',NULL)`)
	for i := 0; i < 2; i++ {
		legacy.Exec(`INSERT INTO EncounteredGames(gameId,puuid,selfPuuid,region,rsoPlatformId,updateAt) VALUES(99,'target','self','TENCENT','NJ100','2026-05-16 10:20:30.000')`)
	}
	legacy.Exec(`INSERT INTO Settings VALUES('custom-main/old-key','{"enabled":true}')`)
	legacy.Exec(`INSERT INTO Settings VALUES('custom-main/current-key','false')`)
	legacy.Exec(`INSERT INTO Settings VALUES('auto-gameflow-main/autoHonorEnabled','true')`)
	legacy.Exec(`INSERT INTO Settings VALUES('window-manager-main/aux-window/autoShow','true')`)
	legacy.Close()
	before, _ := os.ReadFile(path)
	s := testService(t)
	s.settings.Set("custom-main", "current-key", true)
	dto := identity("target", "self")
	dto["tag"] = "current"
	call(t, s, "updatePlayerTag", dto)
	report, err := s.Migration([]string{path})
	if err != nil {
		t.Fatal(err)
	}
	if report.Players != 1 || report.Encounters != 1 || report.Settings != 3 {
		t.Fatal(report)
	}
	if call(t, s, "querySavedPlayer", dto).(map[string]any)["tag"] != "current" || s.settings.Get("custom-main", "current-key") != true {
		t.Fatal("overwrote current")
	}
	if s.settings.Get("auto-gameflow-main", "autoHonorEnabled") != true || s.settings.Get("window-manager-main/aux-window", "autoShow") != true {
		t.Fatal("default or nested namespace migration failed")
	}
	imported := call(t, s, "querySavedPlayer", identity("imported", "self")).(map[string]any)
	if imported["updateAt"] != "2026-05-16T10:20:30.000Z" {
		t.Fatal(imported)
	}
	second, err := s.Migration([]string{path})
	if err != nil || second.Files != 0 {
		t.Fatal(second, err)
	}
	after, _ := os.ReadFile(path)
	if string(after) != string(before) {
		t.Fatal("legacy DB mutated")
	}
	readOnly, err := sqlite.Open(path, true)
	if err != nil {
		t.Fatal(err)
	}
	defer readOnly.Close()
	if _, err := readOnly.Exec(`DELETE FROM SavedPlayers`); err == nil {
		t.Fatal("readonly database permits writing")
	}
}

func TestEarlierMyGoPlayerSettingsMoveToSQLiteAndPreserveCurrentTags(t *testing.T) {
	s := testService(t)
	current := identity("current", "self")
	current["tag"] = "保留新的"
	call(t, s, "updatePlayerTag", current)
	old := identity("current", "self")
	old["tag"] = "旧标签"
	s.settings.Set("saved-player-data", "self:current", old)
	imported := identity("imported", "self")
	imported["tag"] = "早期标记"
	s.settings.Set("saved-player-data", "self:imported", imported)
	s.settings.Set("saved-player-data", "unknown:region", map[string]any{"puuid": "unqualified", "selfPuuid": "self", "tag": "缺区服但需保留"})
	report, err := s.Migration(nil)
	if err != nil {
		t.Fatal(err)
	}
	if report.Players != 1 {
		t.Fatal(report)
	}
	if call(t, s, "querySavedPlayer", current).(map[string]any)["tag"] != "保留新的" || call(t, s, "querySavedPlayer", imported).(map[string]any)["tag"] != "早期标记" {
		t.Fatal("earlier migration lost tags")
	}
	remaining := s.settings.Snapshot("saved-player-data")
	if len(remaining) != 1 || remaining["unknown:region"] == nil {
		t.Fatal("migrated records retained or unknown region discarded")
	}
	second, err := s.Migration(nil)
	if err != nil || second.Players != 0 {
		t.Fatal(second, err)
	}
}

func TestLegacySettingsJSONAffinityPreservesNativeNumbersNullAndJSONText(t *testing.T) {
	path := filepath.Join(t.TempDir(), "LeagueAkari.db")
	legacy, err := sqlite.Open(path, false)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = legacy.Exec(`CREATE TABLE Settings(key varchar PRIMARY KEY,value json)`); err != nil {
		t.Fatal(err)
	}
	fixtures := []struct {
		key, encoded, kind string
		expected           any
	}{
		{"integer", "0", "integer", float64(0)},
		{"real", "2.9", "real", float64(2.9)},
		{"true", "true", "text", true},
		{"false", "false", "text", false},
		{"string", `"中文主题"`, "text", "中文主题"},
		{"empty-string", `""`, "text", ""},
		{"json-null", "null", "text", nil},
		{"object", `{"enabled":true}`, "text", map[string]any{"enabled": true}},
		{"array", `[1,"中文"]`, "text", []any{float64(1), "中文"}},
	}
	for _, fixture := range fixtures {
		if _, err = legacy.Exec(`INSERT INTO Settings(key,value) VALUES(?,?)`, "types-main/"+fixture.key, fixture.encoded); err != nil {
			t.Fatal(err)
		}
		rows, err := legacy.Query(`SELECT typeof(value) AS kind FROM Settings WHERE key=?`, "types-main/"+fixture.key)
		if err != nil || rows[0]["kind"] != fixture.kind {
			t.Fatal("fixture is not stored using real JSON affinity", fixture.key, rows, err)
		}
	}
	legacy.Exec(`INSERT INTO Settings(key,value) VALUES('types-main/sql-null',NULL)`)
	legacy.Close()
	s := testService(t)
	report, err := s.Migration([]string{path})
	if err != nil {
		t.Fatal(err)
	}
	if report.Settings != len(fixtures)+1 {
		t.Fatal(report)
	}
	for _, fixture := range fixtures {
		actual := s.settings.Get("types-main", fixture.key)
		got, _ := json.Marshal(actual)
		expected, _ := json.Marshal(fixture.expected)
		if string(got) != string(expected) {
			t.Fatalf("%s got %s expected %s", fixture.key, got, expected)
		}
	}
	if s.settings.Get("types-main", "sql-null") != nil || !s.settings.HasPersisted("types-main", "sql-null") {
		t.Fatal("SQL NULL did not migrate")
	}
}
