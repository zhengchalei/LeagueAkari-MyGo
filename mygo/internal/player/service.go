package player

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

const Namespace = "saved-player-main"
const DatabaseVersion = 15

type Service struct {
	db           *sqlite.DB
	settings     *settings.Store
	emit         bridge.Emitter
	mu           sync.Mutex
	listeners    map[uint64]func(string, string)
	nextListener uint64
	fileDialog   func(string) (string, error)
}

func New(path string, settingStore *settings.Store, emit bridge.Emitter) (*Service, error) {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return nil, err
	}
	db, err := sqlite.Open(path, false)
	if err != nil {
		return nil, err
	}
	for _, statement := range []string{
		`PRAGMA journal_mode=WAL`,
		`CREATE TABLE IF NOT EXISTS SavedPlayers (puuid TEXT NOT NULL,selfPuuid TEXT NOT NULL,region TEXT NOT NULL,rsoPlatformId TEXT NOT NULL,tag TEXT,updateAt TEXT NOT NULL,lastMetAt TEXT,PRIMARY KEY(puuid,selfPuuid,region,rsoPlatformId))`,
		`CREATE INDEX IF NOT EXISTS saved_players_update_at ON SavedPlayers(updateAt)`,
		`CREATE TABLE IF NOT EXISTS EncounteredGames (id INTEGER PRIMARY KEY AUTOINCREMENT,gameId INTEGER NOT NULL,puuid TEXT NOT NULL,selfPuuid TEXT NOT NULL,region TEXT NOT NULL,rsoPlatformId TEXT NOT NULL,updateAt TEXT NOT NULL,queueType TEXT NOT NULL,UNIQUE(gameId,puuid,selfPuuid,region,rsoPlatformId))`,
		`CREATE INDEX IF NOT EXISTS encountered_player_time ON EncounteredGames(puuid,selfPuuid,updateAt)`,
		`CREATE TABLE IF NOT EXISTS LegacyImports (path TEXT PRIMARY KEY,importedAt TEXT NOT NULL)`,
	} {
		if _, err := db.Exec(statement); err != nil {
			db.Close()
			return nil, err
		}
	}
	return &Service{db: db, settings: settingStore, emit: emit, listeners: map[uint64]func(string, string){}}, nil
}

func (s *Service) Close() error                                      { return s.db.Close() }
func (s *Service) SetFileDialog(dialog func(string) (string, error)) { s.fileDialog = dialog }
func (s *Service) OnChange(fn func(puuid, selfPuuid string)) func() {
	s.mu.Lock()
	s.nextListener++
	id := s.nextListener
	s.listeners[id] = fn
	s.mu.Unlock()
	return func() { s.mu.Lock(); delete(s.listeners, id); s.mu.Unlock() }
}
func (s *Service) changed(puuid, selfPuuid string) {
	s.mu.Lock()
	callbacks := make([]func(string, string), 0, len(s.listeners))
	for _, fn := range s.listeners {
		callbacks = append(callbacks, fn)
	}
	s.mu.Unlock()
	if s.emit != nil {
		s.emit(Namespace, "saved-player-updated", puuid, selfPuuid)
	}
	for _, fn := range callbacks {
		fn(puuid, selfPuuid)
	}
}

type Query struct {
	Puuid         string  `json:"puuid"`
	SelfPuuid     string  `json:"selfPuuid"`
	Region        *string `json:"region"`
	RsoPlatformID *string `json:"rsoPlatformId"`
	QueueType     string  `json:"queueType"`
	Tag           *string `json:"tag"`
	Search        string  `json:"search"`
	TimeOrder     string  `json:"timeOrder"`
	Page          int     `json:"page"`
	PageSize      int     `json:"pageSize"`
}

func decode(value any, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}
func required(query Query) error {
	if query.Puuid == "" || query.SelfPuuid == "" {
		return errors.New("puuid, selfPuuid cannot be empty")
	}
	return nil
}
func now() string                          { return time.Now().UTC().Format("2006-01-02T15:04:05.000Z") }
func stringValue(value any) string         { v, _ := value.(string); return v }
func rowMap(row sqlite.Row) map[string]any { return map[string]any(row) }

func (s *Service) Call(method string, args []any) (any, error) {
	var value any
	if len(args) > 0 {
		value = args[0]
	}
	var query Query
	if method != "deleteEncounteredGame" && method != "importTaggedPlayersFromJsonFile" && method != "exportTaggedPlayersToJsonFile" {
		if err := decode(value, &query); err != nil {
			return nil, err
		}
	}
	switch method {
	case "querySavedPlayer":
		return s.QuerySavedPlayer(query)
	case "querySavedPlayerWithGames":
		return s.QuerySavedPlayerWithGames(query)
	case "saveSavedPlayer":
		return s.savePlayer(value)
	case "updatePlayerTag":
		return s.updateTag(value)
	case "deleteSavedPlayer":
		return s.deletePlayer(query)
	case "getPlayerTags":
		return s.GetPlayerTags(query)
	case "queryEncounteredGames":
		return s.QueryEncounteredGames(query)
	case "saveEncounteredGame":
		return s.saveEncounter(value)
	case "deleteEncounteredGame":
		var id int64
		if err := decode(value, &id); err != nil {
			return nil, err
		}
		rows, err := s.db.Query(`SELECT puuid,selfPuuid FROM EncounteredGames WHERE id=?`, id)
		if err != nil {
			return nil, err
		}
		count, err := s.db.Exec(`DELETE FROM EncounteredGames WHERE id=?`, id)
		if err == nil && len(rows) > 0 {
			s.changed(stringValue(rows[0]["puuid"]), stringValue(rows[0]["selfPuuid"]))
		}
		return map[string]any{"affected": count}, err
	case "queryAllSavedPlayers":
		return s.queryPlayers(query, false)
	case "getAllPlayerTags":
		return s.queryPlayers(query, true)
	case "exportTaggedPlayersToJsonFile", "importTaggedPlayersFromJsonFile":
		path := stringValue(value)
		if path == "" {
			if s.fileDialog == nil {
				return nil, errors.New("player file dialog is not configured")
			}
			kind := "open"
			if method == "exportTaggedPlayersToJsonFile" {
				kind = "save"
			}
			var err error
			path, err = s.fileDialog(kind)
			if err != nil {
				return nil, err
			}
			if path == "" {
				return nil, nil
			}
		}
		if method == "exportTaggedPlayersToJsonFile" {
			return s.ExportTags(path)
		}
		return s.ImportTags(path)
	default:
		return nil, fmt.Errorf("unsupported saved-player method: %s", method)
	}
}
