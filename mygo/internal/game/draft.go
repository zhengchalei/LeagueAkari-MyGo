package game

import (
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func draftState(draft map[string]any) (map[string]any, map[string]any, map[string]any, map[string]any) {
	mode := "CLASSIC"
	if draft["gameModeKind"] == "cherry" {
		mode = "CHERRY"
	}
	stage := map[string]any{"phase": "draft", "gameInfo": map[string]any{"queueId": draft["queueId"], "queueType": mode}}
	positions := map[string]any{}
	for puuid, value := range client.Map(draft["positions"]) {
		positions[puuid] = map[string]any{"position": strings.ToUpper(client.String(client.Map(value)["selected"])), "role": nil}
	}
	return stage, client.Map(draft["teams"]), client.Map(draft["championSelections"]), positions
}

func (s *Service) SetDraft(draft map[string]any) error {
	if len(draft) == 0 || draft["queueId"] == nil || client.Number(draft["queueId"]) < -1 || len(client.Map(draft["teams"])) == 0 {
		return errors.New("draft requires queueId and teams")
	}
	if draft["gameModeKind"] != "normal" && draft["gameModeKind"] != "cherry" {
		return errors.New("draft gameModeKind must be normal or cherry")
	}
	s.refreshMu.Lock()
	defer s.refreshMu.Unlock()
	draft = client.Clone(draft).(map[string]any)
	s.set("draft", draft)
	stage, teams, selections, positions := draftState(draft)
	s.set("queryStage", stage)
	s.set("teams", teams)
	s.set("championSelections", selections)
	s.set("positionAssignments", positions)
	s.set("isInEog", false)
	s.mu.Lock()
	s.loaded = map[string]time.Time{}
	s.historyScope = ""
	s.mu.Unlock()
	return nil
}
func (s *Service) ClearDraft() {
	s.set("draft", nil)
	s.mu.Lock()
	s.loaded = map[string]time.Time{}
	s.historyScope = ""
	s.mu.Unlock()
}

func (s *Service) applyParticipantGroups(view map[string]any) {
	s.mu.RLock()
	draft := s.state["draft"]
	s.mu.RUnlock()
	groups := map[string]any{}
	if draft == nil {
		byPlayer := map[string]int64{}
		game := client.Map(client.Map(client.Map(view["gameflow"])["session"])["gameData"])
		for _, key := range []string{"teamOne", "teamTwo"} {
			for _, value := range client.List(game[key]) {
				row := client.Map(value)
				id := client.Number(row["teamParticipantId"])
				puuid := client.String(row["puuid"])
				if id != 0 && validPUUID(puuid) {
					byPlayer[puuid] = id
				}
			}
		}
		s.mu.RLock()
		for puuid, id := range client.Map(client.Map(s.state["additional"])["teamParticipantGroups"]) {
			byPlayer[puuid] = client.Number(id)
		}
		s.mu.RUnlock()
		for puuid, id := range byPlayer {
			key := fmt.Sprint(id)
			groups[key] = append(client.List(groups[key]), puuid)
		}
		for id, members := range groups {
			ordered := []string{}
			for _, member := range client.List(members) {
				ordered = append(ordered, client.String(member))
			}
			sort.Strings(ordered)
			stable := make([]any, len(ordered))
			for i, member := range ordered {
				stable[i] = member
			}
			groups[id] = stable
		}
	}
	s.set("teamParticipantGroups", groups)
}
