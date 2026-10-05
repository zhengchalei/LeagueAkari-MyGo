package client

import (
	"bytes"
	"encoding/json"
)

// StripUnusedMissions removes only the unused participant progression payload.
// RawMessage preserves all other summary stats, including challenge/tag inputs.
func StripUnusedMissions(summary json.RawMessage) (json.RawMessage, int64, error) {
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(summary, &fields); err != nil {
		return nil, 0, err
	}
	var gameID int64
	_ = json.Unmarshal(fields["gameId"], &gameID)
	changed := false
	var participants []map[string]json.RawMessage
	if err := json.Unmarshal(fields["participants"], &participants); err == nil {
		removed := false
		for _, participant := range participants {
			if _, exists := participant["missions"]; exists {
				delete(participant, "missions")
				removed = true
			}
		}
		if removed {
			data, err := json.Marshal(participants)
			if err != nil {
				return nil, 0, err
			}
			fields["participants"] = data
			changed = true
		}
	}
	// Preserve SGP metadata and pagination around its json game wrappers.
	if raw := fields["json"]; isJSONObject(raw) {
		data, id, err := StripUnusedMissions(raw)
		if err != nil {
			return nil, 0, err
		}
		gameID = id
		if !bytes.Equal(data, raw) {
			fields["json"] = data
			changed = true
		}
	}
	var games []json.RawMessage
	if err := json.Unmarshal(fields["games"], &games); err == nil {
		modified := false
		for index, game := range games {
			if !isJSONObject(game) {
				continue
			}
			data, _, err := StripUnusedMissions(game)
			if err != nil {
				return nil, 0, err
			}
			if !bytes.Equal(data, game) {
				games[index] = data
				modified = true
			}
		}
		if modified {
			data, err := json.Marshal(games)
			if err != nil {
				return nil, 0, err
			}
			fields["games"] = data
			changed = true
		}
	}
	if !changed {
		return summary, gameID, nil
	}
	data, err := json.Marshal(fields)
	return data, gameID, err
}

func isJSONObject(raw json.RawMessage) bool {
	for _, value := range raw {
		switch value {
		case ' ', '\t', '\n', '\r':
			continue
		default:
			return value == '{'
		}
	}
	return false
}
