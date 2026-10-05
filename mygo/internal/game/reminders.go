package game

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func (s *Service) remindTaggedPlayers(ctx context.Context) {
	s.mu.RLock()
	stage := client.Map(s.state["queryStage"])
	draft := s.state["draft"]
	saved := map[string]any{}
	summoners := map[string]any{}
	for puuid, value := range client.Map(s.data["savedInfo"]) {
		saved[puuid] = value
		summoners[puuid] = client.Map(s.data["summoner"])[puuid]
	}
	s.mu.RUnlock()
	if draft != nil || stage["phase"] != "champ-select" {
		s.reminderScope = ""
		s.remindedPlayers = nil
		return
	}
	scope := fmt.Sprintf("%s:%d", s.backend.CurrentServer(), client.Number(client.Map(stage["gameInfo"])["gameId"]))
	if scope != s.reminderScope {
		s.reminderScope = scope
		s.remindedPlayers = map[string]bool{}
	}
	type reminder struct{ puuid, name, tag string }
	pending := []reminder{}
	for puuid, value := range saved {
		if s.remindedPlayers[puuid] {
			continue
		}
		info := client.Map(decodedRaw(value))
		summoner := client.Map(decodedRaw(summoners[puuid]))
		tag := client.String(info["tag"])
		if tag == "" || len(summoner) == 0 {
			continue
		}
		name := client.String(summoner["gameName"]) + "#" + client.String(summoner["tagLine"])
		pending = append(pending, reminder{puuid, name, tag})
	}
	if len(pending) == 0 {
		return
	}
	conversations, err := s.backend.JSON(ctx, http.MethodGet, "/lol-chat/v1/conversations", nil)
	if err != nil {
		return
	}
	room := ""
	for _, value := range client.List(conversations) {
		row := client.Map(value)
		if strings.EqualFold(client.String(row["type"]), "championSelect") {
			room = client.String(row["id"])
			break
		}
	}
	if room == "" {
		return
	}
	for _, player := range pending {
		// League displays celebration messages only in the local conversation.
		// Preserve Akari's reminder behavior without sending an ordinary chat.
		_, err := s.backend.JSON(ctx, http.MethodPost, "/lol-chat/v1/conversations/"+url.PathEscape(room)+"/messages", map[string]any{"body": fmt.Sprintf("[已标记玩家: %s]: \n%s", player.name, player.tag), "fromPid": "", "fromSummonerId": 0, "id": room, "isHistorical": false, "timestamp": "", "type": "celebration"})
		if err == nil {
			s.remindedPlayers[player.puuid] = true
		}
	}
}
