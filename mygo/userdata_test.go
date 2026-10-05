package main

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

func putUserData(t *testing.T, path, value string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(value), 0600); err != nil {
		t.Fatal(err)
	}
}

func TestUserDataMigrationPreservesFilesAndLeavesFormerDirectory(t *testing.T) {
	base := t.TempDir()
	old := filepath.Join(base, "TimoMyGo")
	if err := os.Mkdir(old, 0700); err != nil {
		t.Fatal(err)
	}
	putUserData(t, filepath.Join(old, "settings.json"), `{"namespaces":{"auto-gameflow-main":{"autoHonorEnabled":true}}}`)
	putUserData(t, filepath.Join(old, "window-state.json"), `{"main-window":{"x":20,"y":40,"width":1100,"height":820}}`)
	putUserData(t, filepath.Join(old, "timo.log"), "previous log")
	if err := os.Mkdir(filepath.Join(old, "browser-cache"), 0700); err != nil {
		t.Fatal(err)
	}
	db, err := sqlite.Open(filepath.Join(old, "players.sqlite"), false)
	if err != nil {
		t.Fatal(err)
	}
	db.Exec(`CREATE TABLE Fixture(tag TEXT)`)
	db.Exec(`INSERT INTO Fixture VALUES(?)`, "中文标记")
	db.Close()
	target, err := prepareUserData(base)
	if err != nil {
		t.Fatal(err)
	}
	if filepath.Base(target) != userDataDirectory {
		t.Fatal(target)
	}
	for _, name := range []string{"settings.json", "window-state.json"} {
		before, err := os.ReadFile(filepath.Join(old, name))
		if err != nil {
			t.Fatal(err)
		}
		after, err := os.ReadFile(filepath.Join(target, name))
		if err != nil {
			t.Fatal(err)
		}
		if string(before) != string(after) {
			t.Fatal("changed data", name)
		}
	}
	copy, err := sqlite.Open(filepath.Join(target, "players.sqlite"), true)
	if err != nil {
		t.Fatal(err)
	}
	rows, err := copy.Query(`SELECT tag FROM Fixture`)
	copy.Close()
	if err != nil || len(rows) != 1 || rows[0]["tag"] != "中文标记" {
		t.Fatal("player rows changed during snapshot", rows, err)
	}
	for _, name := range []string{"timo.log", "browser-cache"} {
		if _, err := os.Stat(filepath.Join(target, name)); !os.IsNotExist(err) {
			t.Fatal("unnecessary data migrated", name)
		}
	}
	putUserData(t, filepath.Join(old, "settings.json"), `{"laterOldChange":true}`)
	again, err := prepareUserData(base)
	if err != nil || again != target {
		t.Fatal(again, err)
	}
	data, _ := os.ReadFile(filepath.Join(target, "settings.json"))
	if string(data) == `{"laterOldChange":true}` {
		t.Fatal("second migration overwrote current data")
	}
}

func TestUserDataMigrationSnapshotsCommittedWALWithWriterStillOpen(t *testing.T) {
	base := t.TempDir()
	old := filepath.Join(base, "TimoMyGo")
	os.Mkdir(old, 0700)
	db, err := sqlite.Open(filepath.Join(old, "players.sqlite"), false)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	for _, statement := range []string{`PRAGMA journal_mode=WAL`, `PRAGMA wal_autocheckpoint=0`, `CREATE TABLE Fixture(id INTEGER PRIMARY KEY,tag TEXT)`} {
		if _, err := db.Exec(statement); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := db.Exec(`INSERT INTO Fixture VALUES(1,?)`, "在 WAL 内的标记"); err != nil {
		t.Fatal(err)
	}
	before, err := os.ReadFile(filepath.Join(old, "players.sqlite-wal"))
	if err != nil || len(before) == 0 {
		t.Fatal("fixture has no pending WAL", err)
	}
	target, err := prepareUserData(base)
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"players.sqlite-wal", "players.sqlite-shm"} {
		if _, err := os.Stat(filepath.Join(target, name)); !os.IsNotExist(err) {
			t.Fatal("snapshot retained separate WAL/SHM", name, err)
		}
	}
	after, _ := os.ReadFile(filepath.Join(old, "players.sqlite-wal"))
	if string(before) != string(after) {
		t.Fatal("source WAL modified")
	}
	copy, err := sqlite.Open(filepath.Join(target, "players.sqlite"), true)
	if err != nil {
		t.Fatal(err)
	}
	defer copy.Close()
	rows, err := copy.Query(`SELECT tag FROM Fixture WHERE id=1`)
	if err != nil || len(rows) != 1 || rows[0]["tag"] != "在 WAL 内的标记" {
		t.Fatal("WAL rows lost", rows, err)
	}
}

func TestUserDataMigrationExistingTargetAlwaysWins(t *testing.T) {
	base := t.TempDir()
	old := filepath.Join(base, "TimoMyGo")
	target := filepath.Join(base, userDataDirectory)
	os.Mkdir(old, 0700)
	os.Mkdir(target, 0700)
	putUserData(t, filepath.Join(old, "settings.json"), `{"old":true}`)
	putUserData(t, filepath.Join(target, "settings.json"), `{"new":true}`)
	path, err := prepareUserData(base)
	if err != nil || path != target {
		t.Fatal(path, err)
	}
	data, _ := os.ReadFile(filepath.Join(target, "settings.json"))
	if string(data) != `{"new":true}` {
		t.Fatal("new data overwritten")
	}
}

func TestUserDataMigrationFailureNeverActivatesPartialDirectory(t *testing.T) {
	for _, invalid := range []string{"settings", "database", "orphan-wal"} {
		t.Run(invalid, func(t *testing.T) {
			base := t.TempDir()
			old := filepath.Join(base, "TimoMyGo")
			os.Mkdir(old, 0700)
			name, value := "settings.json", "invalid JSON"
			if invalid == "database" {
				name, value = "players.sqlite", "broken SQLite image"
			}
			if invalid == "orphan-wal" {
				name, value = "players.sqlite-wal", "orphan journal"
			}
			putUserData(t, filepath.Join(old, name), value)
			if _, err := prepareUserData(base); err == nil {
				t.Fatal("invalid migration succeeded")
			}
			if _, err := os.Stat(filepath.Join(base, userDataDirectory)); !os.IsNotExist(err) {
				t.Fatal("partial target activated", err)
			}
			entries, err := os.ReadDir(base)
			if err != nil || len(entries) != 1 || entries[0].Name() != "TimoMyGo" {
				t.Fatal("staging directory retained", entries, err)
			}
			data, _ := os.ReadFile(filepath.Join(old, name))
			if string(data) != value {
				t.Fatal("old data changed")
			}
		})
	}
}

func TestUserDataWithoutPreviousInstallationCreatesFreshDirectory(t *testing.T) {
	base := t.TempDir()
	path, err := prepareUserData(base)
	if err != nil {
		t.Fatal(err)
	}
	if path != filepath.Join(base, userDataDirectory) {
		t.Fatal(path)
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatal(err)
	}
}
