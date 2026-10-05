package player

import (
	"encoding/json"
	"errors"
	"os"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

type tagDocument struct {
	DatabaseVersion int              `json:"databaseVersion"`
	Type            string           `json:"type"`
	Data            []map[string]any `json:"data"`
}

func (s *Service) ExportTags(path string) (any, error) {
	rows, err := s.db.Query(`SELECT puuid,selfPuuid,region,rsoPlatformId,tag FROM SavedPlayers WHERE tag IS NOT NULL ORDER BY updateAt DESC`)
	if err != nil {
		return nil, err
	}
	doc := tagDocument{DatabaseVersion: DatabaseVersion, Type: "league-akari-tagged-players", Data: []map[string]any{}}
	for _, row := range rows {
		doc.Data = append(doc.Data, rowMap(row))
	}
	data, err := json.MarshalIndent(doc, "", "  ")
	if err != nil {
		return nil, err
	}
	if err := os.WriteFile(path, data, 0600); err != nil {
		return nil, err
	}
	return path, nil
}

func (s *Service) ImportTags(path string) (any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var doc tagDocument
	if err := json.Unmarshal(data, &doc); err != nil {
		return nil, err
	}
	if doc.Type != "league-akari-tagged-players" || doc.Data == nil {
		return nil, errors.New("InvalidTaggedPlayersFile")
	}
	if doc.DatabaseVersion > DatabaseVersion {
		return nil, errors.New("InvalidDatabaseVersion")
	}
	for _, row := range doc.Data {
		for _, key := range []string{"puuid", "selfPuuid", "region", "rsoPlatformId", "tag"} {
			if _, ok := row[key].(string); !ok {
				return nil, errors.New("InvalidTaggedPlayersData")
			}
		}
		if row["puuid"] == "" || row["selfPuuid"] == "" || row["region"] == "" {
			return nil, errors.New("InvalidTaggedPlayersData")
		}
	}
	stamp := now()
	err = s.db.Transaction(func(tx *sqlite.Conn) error {
		for _, row := range doc.Data {
			if _, err := tx.Exec(`INSERT INTO SavedPlayers(puuid,selfPuuid,region,rsoPlatformId,tag,updateAt) VALUES(?,?,?,?,?,?) ON CONFLICT(puuid,selfPuuid,region,rsoPlatformId) DO UPDATE SET tag=excluded.tag,updateAt=excluded.updateAt`, row["puuid"], row["selfPuuid"], row["region"], row["rsoPlatformId"], row["tag"], stamp); err != nil {
				return err
			}
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	for _, row := range doc.Data {
		s.changed(stringValue(row["puuid"]), stringValue(row["selfPuuid"]))
	}
	return path, nil
}
