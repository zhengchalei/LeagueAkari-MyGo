package player

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

type MigrationReport struct {
	Files      int      `json:"files"`
	Players    int      `json:"players"`
	Encounters int      `json:"encounters"`
	Settings   int      `json:"settings"`
	Skipped    []string `json:"skipped"`
}

// Each source is opened read-only. Current MyGo rows/settings always take precedence.
func (s *Service) Migration(paths []string) (MigrationReport, error) {
	report := MigrationReport{Skipped: []string{}}
	if err := s.importEarlyMyGoPlayers(&report); err != nil {
		return report, err
	}
	for _, path := range paths {
		absolute, err := filepath.Abs(path)
		if err != nil {
			return report, err
		}
		if _, err = os.Stat(absolute); errors.Is(err, os.ErrNotExist) {
			report.Skipped = append(report.Skipped, absolute)
			continue
		} else if err != nil {
			return report, err
		}
		known, err := s.db.Query(`SELECT path FROM LegacyImports WHERE path=?`, absolute)
		if err != nil {
			return report, err
		}
		if len(known) > 0 {
			report.Skipped = append(report.Skipped, absolute)
			continue
		}
		legacy, err := sqlite.Open(absolute, true)
		if err != nil {
			return report, fmt.Errorf("read legacy database %s: %w", absolute, err)
		}
		err = s.importLegacy(legacy, absolute, &report)
		closeErr := legacy.Close()
		if err != nil {
			return report, err
		}
		if closeErr != nil {
			return report, closeErr
		}
		report.Files++
	}
	return report, nil
}

func (s *Service) importEarlyMyGoPlayers(report *MigrationReport) error {
	if s.settings == nil {
		return nil
	}
	entries := s.settings.Snapshot("saved-player-data")
	if len(entries) == 0 {
		return nil
	}
	known, err := s.db.Query(`SELECT path FROM LegacyImports WHERE path='mygo-settings:saved-player-data'`)
	if err != nil || len(known) > 0 {
		return err
	}
	rows := []map[string]any{}
	keys := []string{}
	for key, value := range entries {
		row, ok := value.(map[string]any)
		if !ok || stringValue(row["puuid"]) == "" || stringValue(row["selfPuuid"]) == "" {
			report.Skipped = append(report.Skipped, "mygo-settings:saved-player-data/"+key)
			continue
		}
		region := stringValue(row["region"])
		if region == "" {
			report.Skipped = append(report.Skipped, "mygo-settings:saved-player-data/"+key)
			continue
		} // The first prototype did not retain region identity.
		platform := stringValue(row["rsoPlatformId"])
		if tag := row["tag"]; tag != nil {
			if _, ok := tag.(string); !ok {
				continue
			}
		}
		stamp := legacyTime(row["updateAt"])
		if stamp == nil || stamp == "" {
			stamp = now()
		}
		rows = append(rows, map[string]any{"puuid": row["puuid"], "selfPuuid": row["selfPuuid"], "region": region, "rsoPlatformId": platform, "tag": row["tag"], "updateAt": stamp, "lastMetAt": legacyTime(row["lastMetAt"])})
		keys = append(keys, key)
	}
	count := 0
	if err := s.db.Transaction(func(tx *sqlite.Conn) error {
		for _, row := range rows {
			inserted, err := tx.Exec(`INSERT OR IGNORE INTO SavedPlayers(puuid,selfPuuid,region,rsoPlatformId,tag,updateAt,lastMetAt) VALUES(?,?,?,?,?,?,?)`, row["puuid"], row["selfPuuid"], row["region"], row["rsoPlatformId"], row["tag"], row["updateAt"], row["lastMetAt"])
			if err != nil {
				return err
			}
			count += int(inserted)
		}
		_, err := tx.Exec(`INSERT INTO LegacyImports(path,importedAt) VALUES('mygo-settings:saved-player-data',?)`, now())
		return err
	}); err != nil {
		return err
	}
	report.Players += count
	// Only remove the obsolete JSON records after the independent database commit.
	for _, key := range keys {
		if err := s.settings.Delete("saved-player-data", key); err != nil {
			return err
		}
	}
	return nil
}

func legacyRows(db *sqlite.DB, table string) ([]sqlite.Row, error) {
	rows, err := db.Query(`SELECT name FROM sqlite_master WHERE type='table' AND name=?`, table)
	if err != nil || len(rows) == 0 {
		return []sqlite.Row{}, err
	}
	return db.Query(`SELECT * FROM ` + sqlite.QuoteIdentifier(table))
}

func legacyTime(value any) any {
	if value == nil {
		return nil
	}
	text := stringValue(value)
	for _, layout := range []string{time.RFC3339Nano, "2006-01-02 15:04:05.999999999", "2006-01-02 15:04:05"} {
		if stamp, err := time.Parse(layout, text); err == nil {
			return stamp.UTC().Format("2006-01-02T15:04:05.000Z")
		}
	}
	return text
}
func legacyPlatform(row sqlite.Row) any {
	if row["rsoPlatformId"] != nil {
		return row["rsoPlatformId"]
	}
	return ""
}

func legacySettingValue(stored any) (any, error) {
	// TypeORM's JSON column has SQLite numeric affinity: JSON numbers can be
	// stored as INTEGER/REAL, while strings, booleans and containers remain TEXT.
	if text, ok := stored.(string); ok {
		var value any
		if err := json.Unmarshal([]byte(text), &value); err != nil {
			return nil, err
		}
		return value, nil
	}
	switch stored.(type) {
	case nil, int64, float64, bool:
		return stored, nil
	default:
		return nil, fmt.Errorf("unsupported legacy JSON storage type %T", stored)
	}
}

func (s *Service) importLegacy(legacy *sqlite.DB, path string, report *MigrationReport) error {
	players, err := legacyRows(legacy, "SavedPlayers")
	if err != nil {
		return err
	}
	encounters, err := legacyRows(legacy, "EncounteredGames")
	if err != nil {
		return err
	}
	settingRows, err := legacyRows(legacy, "Settings")
	if err != nil {
		return err
	}
	// Validate all JSON before copying any setting from a source file.
	type setting struct {
		namespace, key string
		value          any
	}
	values := []setting{}
	for _, row := range settingRows {
		key := stringValue(row["key"])
		index := strings.LastIndex(key, "/")
		if index < 1 {
			continue
		}
		value, err := legacySettingValue(row["value"])
		if err != nil {
			return fmt.Errorf("legacy setting %s: %w", key, err)
		}
		values = append(values, setting{key[:index], key[index+1:], value})
	}
	err = s.db.Transaction(func(tx *sqlite.Conn) error {
		for _, row := range players {
			if row["puuid"] == nil || row["selfPuuid"] == nil || row["region"] == nil {
				return errors.New("legacy saved player has incomplete identity")
			}
			count, err := tx.Exec(`INSERT OR IGNORE INTO SavedPlayers(puuid,selfPuuid,region,rsoPlatformId,tag,updateAt,lastMetAt) VALUES(?,?,?,?,?,?,?)`, row["puuid"], row["selfPuuid"], row["region"], legacyPlatform(row), row["tag"], legacyTime(row["updateAt"]), legacyTime(row["lastMetAt"]))
			if err != nil {
				return err
			}
			report.Players += int(count)
		}
		for _, row := range encounters {
			queue := row["queueType"]
			if queue == nil {
				queue = ""
			}
			count, err := tx.Exec(`INSERT OR IGNORE INTO EncounteredGames(gameId,puuid,selfPuuid,region,rsoPlatformId,updateAt,queueType) VALUES(?,?,?,?,?,?,?)`, row["gameId"], row["puuid"], row["selfPuuid"], row["region"], legacyPlatform(row), legacyTime(row["updateAt"]), queue)
			if err != nil {
				return err
			}
			report.Encounters += int(count)
		}
		return nil
	})
	if err != nil {
		return err
	}
	if s.settings != nil {
		for _, value := range values {
			if s.settings.HasPersisted(value.namespace, value.key) {
				continue
			}
			if err := s.settings.Set(value.namespace, value.key, value.value); err != nil {
				return err
			}
			report.Settings++
		}
	}
	_, err = s.db.Exec(`INSERT INTO LegacyImports(path,importedAt) VALUES(?,?)`, path, now())
	return err
}
