package game

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func emptyAdditional() map[string]any {
	return map[string]any{"teams": map[string]any{}, "selections": map[string]any{}, "positions": map[string]any{}, "spells": map[string]any{}, "teamParticipantGroups": map[string]any{}}
}

func parseSelectedRole(value string) map[string]any {
	role := map[string]any{"current": "NONE", "assignmentReason": "NONE", "primary": "NONE", "secondary": "NONE", "fill": "NONE"}
	segments := strings.Split(value, ".")
	if len(segments) != 4 && len(segments) != 5 {
		return role
	}
	for i, key := range []string{"current", "assignmentReason", "primary", "secondary", "fill"} {
		if i < len(segments) && segments[i] != "" {
			role[key] = segments[i]
		}
	}
	return role
}

func (s *Service) refreshAdditionalRoster(ctx context.Context, puuid string, info map[string]any) map[string]any {
	scope := fmt.Sprintf("%s:%s:%d", s.backend.CurrentServer(), puuid, client.Number(info["gameId"]))
	if s.rosterScope != scope {
		s.additionalRoster = nil
		s.rosterFetchedAt = time.Time{}
		s.rosterScope = scope
	}
	if validPUUID(puuid) && s.additionalRoster == nil && (s.rosterFetchedAt.IsZero() || time.Since(s.rosterFetchedAt) > 30*time.Second) {
		s.rosterFetchedAt = time.Now()
		value, err := s.backend.SGPJSON(ctx, "", "league-session", http.MethodGet, "/gsm/v1/ledge/region/@akari:sgpServerSubId@/puuid/"+url.PathEscape(puuid), nil)
		if err == nil {
			game := client.Map(client.Map(value)["game"])
			if len(client.List(game["teamOne"]))+len(client.List(game["teamTwo"])) > 0 {
				s.additionalRoster = game
			}
		}
	}
	additional := emptyAdditional()
	game := s.additionalRoster
	mode := client.String(game["gameMode"])
	for i, key := range []string{"teamOne", "teamTwo"} {
		team := fmt.Sprintf("TEAM-%d", 100+i*100)
		if mode == "CHERRY" || info["queueType"] == "CHERRY" || info["gameMode"] == "CHERRY" {
			team = "TEAM-ALL"
		}
		for _, value := range client.List(game[key]) {
			row := client.Map(value)
			puuid := client.String(row["puuid"])
			if !validPUUID(puuid) {
				continue
			}
			client.Map(additional["teams"])[team] = append(client.List(client.Map(additional["teams"])[team]), puuid)
			client.Map(additional["selections"])[puuid] = client.Number(row["championId"])
			if id := client.Number(row["teamParticipantId"]); id != 0 || mode == "CHERRY" {
				client.Map(additional["teamParticipantGroups"])[puuid] = id
			}
			client.Map(additional["positions"])[puuid] = map[string]any{"position": strings.ToUpper(client.String(row["selectedPosition"])), "role": parseSelectedRole(client.String(row["selectedRole"]))}
		}
	}
	for _, value := range client.List(game["playerChampionSelections"]) {
		row := client.Map(value)
		puuid := client.String(row["puuid"])
		if validPUUID(puuid) {
			client.Map(additional["spells"])[puuid] = map[string]any{"spell1Id": row["spell1Id"], "spell2Id": row["spell2Id"]}
		}
	}
	s.set("additional", additional)
	return additional
}
