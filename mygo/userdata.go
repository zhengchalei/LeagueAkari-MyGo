package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

const userDataDirectory = "LeagueAkari-MyGo"

var migratedUserDataFiles = []string{"settings.json", "players.sqlite", "window-state.json"}

// SQLite takes a consistent snapshot including committed WAL records even when
// the former app is running. A completed new directory always wins.
func prepareUserData(configDir string) (string, error) {
	target := filepath.Join(configDir, userDataDirectory)
	if exists, err := existingUserData(target); err != nil || exists {
		return target, err
	}
	source := filepath.Join(configDir, "TimoMyGo")
	if exists, err := existingUserData(source); err != nil {
		return "", err
	} else if !exists {
		return target, os.MkdirAll(target, 0700)
	}
	stage, err := os.MkdirTemp(configDir, ".LeagueAkari-MyGo-migration-")
	if err != nil {
		return "", err
	}
	defer os.RemoveAll(stage)
	for _, name := range migratedUserDataFiles {
		if name == "players.sqlite" {
			if err := snapshotPlayerDatabase(filepath.Join(source, name), filepath.Join(stage, name)); err != nil {
				return "", fmt.Errorf("snapshot previous player database: %w", err)
			}
			continue
		}
		if err := copyUserDataFile(filepath.Join(source, name), filepath.Join(stage, name)); err != nil {
			return "", fmt.Errorf("copy previous user data %s: %w", name, err)
		}
	}
	if err := validateUserData(stage); err != nil {
		return "", fmt.Errorf("validate migrated user data: %w", err)
	}
	// Another instance may have established the new directory while copying.
	if exists, err := existingUserData(target); err != nil || exists {
		return target, err
	}
	if err := os.Rename(stage, target); err != nil {
		if exists, checkErr := existingUserData(target); checkErr == nil && exists {
			return target, nil
		}
		return "", err
	}
	return target, nil
}

func snapshotPlayerDatabase(source, target string) error {
	info, err := os.Stat(source)
	if errors.Is(err, os.ErrNotExist) {
		for _, suffix := range []string{"-wal", "-shm"} {
			if _, err := os.Stat(source + suffix); err == nil {
				return errors.New("player database journal exists without its database")
			}
		}
		return nil
	}
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return errors.New("expected a regular player database")
	}
	db, err := sqlite.Open(source, true)
	if err != nil {
		return err
	}
	// VACUUM INTO reads one transaction from the source and writes a standalone
	// destination. Copying the live database/WAL/SHM separately is not atomic.
	_, backupErr := db.Exec("VACUUM INTO ?", target)
	closeErr := db.Close()
	if backupErr != nil {
		return backupErr
	}
	return closeErr
}

func existingUserData(path string) (bool, error) {
	info, err := os.Stat(path)
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	if !info.IsDir() {
		return false, fmt.Errorf("user data path is not a directory: %s", path)
	}
	return true, nil
}

func copyUserDataFile(source, target string) error {
	input, err := os.Open(source)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	defer input.Close()
	info, err := input.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return errors.New("expected a regular data file")
	}
	output, err := os.OpenFile(target, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	if _, err = io.Copy(output, input); err != nil {
		output.Close()
		return err
	}
	if err = output.Sync(); err != nil {
		output.Close()
		return err
	}
	return output.Close()
}

func validateUserData(path string) error {
	for _, name := range []string{"settings.json", "window-state.json"} {
		data, err := os.ReadFile(filepath.Join(path, name))
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			return err
		}
		if !json.Valid(data) {
			return fmt.Errorf("%s contains invalid JSON", name)
		}
	}
	dbPath := filepath.Join(path, "players.sqlite")
	if _, err := os.Stat(dbPath); errors.Is(err, os.ErrNotExist) {
		for _, name := range []string{"players.sqlite-wal", "players.sqlite-shm"} {
			if _, err := os.Stat(filepath.Join(path, name)); err == nil {
				return errors.New("player database journal exists without its database")
			}
		}
		return nil
	} else if err != nil {
		return err
	}
	db, err := sqlite.Open(dbPath, true)
	if err != nil {
		return err
	}
	rows, checkErr := db.Query("PRAGMA integrity_check")
	closeErr := db.Close()
	if checkErr != nil {
		return checkErr
	}
	if closeErr != nil {
		return closeErr
	}
	if len(rows) != 1 || rows[0]["integrity_check"] != "ok" {
		return errors.New("player database failed integrity check")
	}
	return nil
}
