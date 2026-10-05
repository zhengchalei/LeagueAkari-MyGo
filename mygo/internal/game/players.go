package game

import (
	"context"
	"net/http"
	"strconv"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func validPUUID(value string) bool {
	return value != "" && value != "00000000-0000-0000-0000-000000000000"
}

func (s *Service) collect(ctx context.Context, view map[string]any) (map[string]any, map[string]any, map[string]any, map[string]any) {
	s.mu.RLock()
	draft := client.Map(client.Clone(s.state["draft"]))
	s.mu.RUnlock()
	if len(draft) > 0 {
		s.set("additional", emptyAdditional())
		return draftState(draft)
	}
	teams := map[string]any{}
	selections := map[string]any{}
	positions := map[string]any{}
	flow := client.Map(view["gameflow"])
	phase := client.String(flow["phase"])
	gameData := client.Map(client.Map(flow["session"])["gameData"])
	queue := client.Map(gameData["queue"])
	gameInfo := map[string]any{"queueId": queue["id"], "queueType": queue["type"], "gameMode": queue["gameMode"], "gameId": gameData["gameId"]}
	if gameInfo["gameMode"] == nil {
		gameInfo["gameMode"] = gameData["gameMode"]
	}
	stage := map[string]any{"phase": "unavailable", "gameInfo": nil}
	if client.String(client.Map(view["state"])["connectionState"]) != "connected" {
		s.set("additional", emptyAdditional())
		return stage, teams, selections, positions
	}
	add := func(team string, value any) {
		if gameInfo["gameMode"] == "CHERRY" || gameInfo["queueType"] == "CHERRY" {
			team = "TEAM-ALL"
		}
		row := client.Map(value)
		puuid := client.String(row["puuid"])
		if !validPUUID(puuid) && client.Number(row["summonerId"]) > 0 {
			if summoner, err := s.backend.JSON(ctx, http.MethodGet, "/lol-summoner/v1/summoners/"+strconv.FormatInt(client.Number(row["summonerId"]), 10), nil); err == nil {
				puuid = client.String(client.Map(summoner)["puuid"])
			}
		}
		if !validPUUID(puuid) {
			return
		}
		members := client.List(teams[team])
		for _, member := range members {
			if member == puuid {
				return
			}
		}
		teams[team] = append(members, puuid)
		selections[puuid] = client.Number(row["championId"])
		position := client.String(row["assignedPosition"])
		if position == "" {
			position = client.String(row["selectedPosition"])
		}
		var role any
		if row["selectedRole"] != nil {
			role = parseSelectedRole(client.String(row["selectedRole"]))
		}
		positions[puuid] = map[string]any{"position": strings.ToUpper(position), "role": role}
	}
	selectSession := client.Map(client.Map(view["champSelect"])["session"])
	if len(selectSession) > 0 {
		s.set("additional", emptyAdditional())
		if client.Number(gameInfo["gameId"]) == 0 {
			gameInfo["gameId"] = selectSession["gameId"]
		}
		if client.Number(gameInfo["queueId"]) == 0 {
			gameInfo["queueId"] = selectSession["queueId"]
		}
		stage = map[string]any{"phase": "champ-select", "gameInfo": gameInfo}
		for _, row := range client.List(selectSession["myTeam"]) {
			add("TEAM-100", row)
		}
		for _, row := range client.List(selectSession["theirTeam"]) {
			add("TEAM-200", row)
		}
		s.mu.Lock()
		s.lastChampSelect = client.Clone(map[string]any{"teams": teams, "selections": selections, "positions": positions}).(map[string]any)
		s.lastGameID = client.Number(gameInfo["gameId"])
		s.mu.Unlock()
		return stage, teams, selections, positions
	}
	if client.IsInGame(phase) {
		stage = map[string]any{"phase": "in-game", "gameInfo": gameInfo}
		for _, row := range client.List(gameData["teamOne"]) {
			add("TEAM-100", row)
		}
		for _, row := range client.List(gameData["teamTwo"]) {
			add("TEAM-200", row)
		}
		// GSM also supplies party IDs, positions and arena teams missing from LCU.
		me := client.Map(client.Map(view["summoner"])["me"])
		additional := s.refreshAdditionalRoster(ctx, client.String(me["puuid"]), gameInfo)
		if _, arena := client.Map(additional["teams"])["TEAM-ALL"]; arena {
			all := []any{}
			seen := map[string]bool{}
			for _, members := range teams {
				for _, member := range client.List(members) {
					key := client.String(member)
					if !seen[key] {
						all = append(all, member)
						seen[key] = true
					}
				}
			}
			teams = map[string]any{"TEAM-ALL": all}
			gameInfo["queueType"] = "CHERRY"
			gameInfo["gameMode"] = "CHERRY"
		}
		for team, members := range client.Map(additional["teams"]) {
			for _, value := range client.List(members) {
				puuid := client.String(value)
				add(team, map[string]any{"puuid": puuid, "championId": client.Map(additional["selections"])[puuid]})
			}
		}
		for puuid, value := range client.Map(additional["selections"]) {
			if client.Number(value) != 0 {
				selections[puuid] = value
			}
		}
		for puuid, value := range client.Map(additional["positions"]) {
			positions[puuid] = value
		}
		for _, value := range client.List(gameData["playerChampionSelections"]) {
			row := client.Map(value)
			puuid := client.String(row["puuid"])
			if validPUUID(puuid) {
				selections[puuid] = row["championId"]
			}
		}
		s.mu.RLock()
		snapshot := client.Map(client.Clone(s.lastChampSelect))
		snapshotID := s.lastGameID
		s.mu.RUnlock()
		if snapshotID == client.Number(gameInfo["gameId"]) {
			for team, members := range client.Map(snapshot["teams"]) {
				if gameInfo["gameMode"] == "CHERRY" || gameInfo["queueType"] == "CHERRY" {
					team = "TEAM-ALL"
				}
				for _, puuid := range client.List(members) {
					missing := true
					for _, current := range client.List(teams[team]) {
						if current == puuid {
							missing = false
						}
					}
					if missing {
						teams[team] = append(client.List(teams[team]), puuid)
					}
				}
			}
			for puuid, value := range client.Map(snapshot["selections"]) {
				if _, exists := selections[puuid]; !exists {
					selections[puuid] = value
				}
			}
			for puuid, value := range client.Map(snapshot["positions"]) {
				if _, exists := positions[puuid]; !exists {
					positions[puuid] = value
				}
			}
		}
		return stage, teams, selections, positions
	}
	lobby := client.Map(client.Map(view["lobby"])["lobby"])
	s.set("additional", emptyAdditional())
	if len(lobby) > 0 {
		config := client.Map(lobby["gameConfig"])
		stage = map[string]any{"phase": "lobby", "gameInfo": map[string]any{"queueId": config["queueId"], "queueType": config["gameMode"]}}
		for _, row := range client.List(lobby["members"]) {
			add("LOBBY", row)
		}
	}
	s.mu.Lock()
	s.lastChampSelect = nil
	s.lastGameID = 0
	s.mu.Unlock()
	return stage, teams, selections, positions
}
