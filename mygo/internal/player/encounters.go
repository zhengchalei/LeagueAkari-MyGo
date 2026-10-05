package player

import (
	"errors"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/sqlite"
)

type Encounter struct {
	GameID        int64  `json:"gameId"`
	Puuid         string `json:"puuid"`
	SelfPuuid     string `json:"selfPuuid"`
	Region        string `json:"region"`
	RsoPlatformID string `json:"rsoPlatformId"`
	QueueType     string `json:"queueType"`
}

func (s *Service) saveEncounter(value any) (any, error) {
	var dto Encounter
	if err := decode(value, &dto); err != nil {
		return nil, err
	}
	return s.RecordEncounter(dto)
}

// A finished game is recorded once per account/player/region, even after app restart.
func (s *Service) RecordEncounter(dto Encounter) (any, error) {
	if dto.GameID <= 0 || dto.Puuid == "" || dto.SelfPuuid == "" || dto.Region == "" {
		return nil, errors.New("gameId, puuid, selfPuuid, region are required")
	}
	stamp := now()
	var result map[string]any
	err := s.db.Transaction(func(tx *sqlite.Conn) error {
		count, err := tx.Exec(`INSERT OR IGNORE INTO EncounteredGames(gameId,puuid,selfPuuid,region,rsoPlatformId,updateAt,queueType) VALUES(?,?,?,?,?,?,?)`, dto.GameID, dto.Puuid, dto.SelfPuuid, dto.Region, dto.RsoPlatformID, stamp, dto.QueueType)
		if err != nil {
			return err
		}
		if count > 0 {
			_, err = tx.Exec(`INSERT INTO SavedPlayers(puuid,selfPuuid,region,rsoPlatformId,tag,updateAt,lastMetAt) VALUES(?,?,?,?,NULL,?,?) ON CONFLICT(puuid,selfPuuid,region,rsoPlatformId) DO UPDATE SET updateAt=excluded.updateAt,lastMetAt=excluded.lastMetAt`, dto.Puuid, dto.SelfPuuid, dto.Region, dto.RsoPlatformID, stamp, stamp)
			if err != nil {
				return err
			}
		}
		rows, err := tx.Query(`SELECT * FROM EncounteredGames WHERE gameId=? AND puuid=? AND selfPuuid=? AND region=? AND rsoPlatformId=?`, dto.GameID, dto.Puuid, dto.SelfPuuid, dto.Region, dto.RsoPlatformID)
		if err != nil {
			return err
		}
		result = rowMap(rows[0])
		return nil
	})
	if err == nil {
		s.changed(dto.Puuid, dto.SelfPuuid)
	}
	return result, err
}
