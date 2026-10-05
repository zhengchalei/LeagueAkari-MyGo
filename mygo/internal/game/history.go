package game

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type rawBackend interface {
	JSONRaw(context.Context, string, string, any) (json.RawMessage, error)
	SGPJSONRaw(context.Context, string, string, string, string, any) (json.RawMessage, error)
}

type summaryWrapper struct {
	Source string          `json:"source"`
	GameID int64           `json:"gameId"`
	Data   json.RawMessage `json:"data"`
}

// Preserve every renderer-consumed stat, including challenges used by tags.
// missions is unrelated progression payload and can dominate each participant.
func stripUnusedMissions(summary json.RawMessage) (json.RawMessage, int64, error) {
	return client.StripUnusedMissions(summary)
}

func compactHistory(data json.RawMessage, sgp bool) ([]summaryWrapper, error) {
	var envelope struct {
		Games json.RawMessage `json:"games"`
	}
	if err := json.Unmarshal(data, &envelope); err != nil {
		return nil, err
	}
	var rawGames []json.RawMessage
	if sgp {
		if err := json.Unmarshal(envelope.Games, &rawGames); err != nil {
			return nil, err
		}
	} else {
		var nested struct {
			Games []json.RawMessage `json:"games"`
		}
		if err := json.Unmarshal(envelope.Games, &nested); err != nil {
			return nil, err
		}
		rawGames = nested.Games
	}
	games := make([]summaryWrapper, 0, len(rawGames))
	for _, rawGame := range rawGames {
		if sgp {
			var game map[string]json.RawMessage
			if err := json.Unmarshal(rawGame, &game); err != nil {
				return nil, err
			}
			if len(game["json"]) == 0 || string(game["json"]) == "null" {
				continue
			}
			compacted, id, err := stripUnusedMissions(game["json"])
			if err != nil {
				return nil, err
			}
			game["json"] = compacted
			stored, err := json.Marshal(game)
			if err != nil {
				return nil, err
			}
			games = append(games, summaryWrapper{Source: "sgp", GameID: id, Data: stored})
		} else {
			compacted, id, err := stripUnusedMissions(rawGame)
			if err != nil {
				return nil, err
			}
			games = append(games, summaryWrapper{Source: "lcu", GameID: id, Data: compacted})
		}
	}
	return games, nil
}

func (s *Service) history(ctx context.Context, puuid string, count int, query url.Values) (any, string, error) {
	source := s.config().source
	sgp := "/match-history-query/v1/products/lol/player/" + url.PathEscape(puuid) + "/SUMMARY?" + query.Encode()
	lcu := fmt.Sprintf("/lol-match-history/v1/products/lol/%s/matches?begIndex=0&endIndex=%d", url.PathEscape(puuid), count-1)
	path := sgp
	if source == "lcu" {
		path = lcu
	}
	data, err := s.queryJSON(ctx, source, "", path)
	if err != nil {
		if source == "sgp" {
			source, path = "lcu", lcu
		} else {
			source, path = "sgp", sgp
		}
		data, err = s.queryJSON(ctx, source, "", path)
	}
	if err != nil {
		return nil, source, err
	}
	encoded, err := json.Marshal(data)
	if err != nil {
		return nil, source, err
	}
	games, err := compactHistory(encoded, source == "sgp")
	return games, source, err
}

func decodedRaw(value any) any {
	if data, ok := value.(json.RawMessage); ok {
		var decoded any
		_ = json.Unmarshal(data, &decoded)
		return decoded
	}
	return client.Clone(value)
}
