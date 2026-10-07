package main

import (
	"math"
	"testing"
)

func TestPresetCsShareUsesOwnTeamAndPerGameAverage(t *testing.T) {
	for _, source := range []string{"sgp", "lcu"} {
		t.Run(source, func(t *testing.T) {
			makeGame := func(ownCs, ownJungle, allyCs, allyJungle float64) object {
				participants := []any{}
				identities := []any{}
				for i, sample := range []struct {
					puuid      string
					team       int
					cs, jungle float64
				}{{"self", 100, ownCs, ownJungle}, {"ally", 100, allyCs, allyJungle}, {"enemy", 200, 1000, 1000}} {
					stats := object{"totalMinionsKilled": sample.cs, "neutralMinionsKilled": sample.jungle, "win": true}
					p := object{"participantId": i + 1, "teamId": sample.team, "championId": 64}
					if source == "sgp" {
						p["puuid"] = sample.puuid
						for key, value := range stats {
							p[key] = value
						}
					} else {
						p["stats"] = stats
						identities = append(identities, object{"participantId": i + 1, "player": object{"puuid": sample.puuid}})
					}
					participants = append(participants, p)
				}
				game := object{"gameDuration": 600, "gameMode": "CLASSIC", "participants": participants, "participantIdentities": identities}
				if source == "sgp" {
					game = object{"json": game}
				}
				return object{"source": source, "data": game}
			}
			// The original averages each game's share (.5 and .25), rather than pooling CS.
			data := object{"matchHistory": object{"self": object{"data": []any{makeGame(20, 30, 40, 10), makeGame(30, 20, 100, 50)}}}}
			analysis := presetAnalyzeHistory(data, "self")
			for _, summary := range []object{presetObject(analysis["summary"]), presetObject(presetPath(analysis, "champions", "64", "summary"))} {
				if got := presetN(summary["avgCsPercentageOfTeam"]); math.Abs(got-.375) > 1e-12 {
					t.Fatalf("CS team share = %v; want .375", got)
				}
			}
			data = object{"matchHistory": object{"self": object{"data": []any{makeGame(0, 0, 0, 0)}}}}
			if got := presetN(presetPath(presetAnalyzeHistory(data, "self"), "summary", "avgCsPercentageOfTeam")); got != 0 || math.IsNaN(got) {
				t.Fatalf("empty team's share = %v; want 0", got)
			}
		})
	}
}
