package game

import (
	"context"
	"encoding/json"
	"fmt"
	"strconv"
	"sync"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

// Encounter pages are small; decode just their identifiers, never ten full histories.
func (s *Service) savedInfoReferences() (map[int64]bool, map[string]bool) {
	s.mu.RLock()
	values := make([]any, 0, len(client.Map(s.data["savedInfo"])))
	active := map[string]bool{}
	for _, members := range client.Map(s.state["teams"]) {
		for _, member := range client.List(members) {
			active[client.String(member)] = true
		}
	}
	for puuid, value := range client.Map(s.data["savedInfo"]) {
		if active[puuid] {
			values = append(values, value)
		}
	}
	s.mu.RUnlock()
	games := map[int64]bool{}
	authors := map[string]bool{}
	for _, value := range values {
		var info struct {
			EncounteredGames struct {
				Data []struct {
					GameID int64 `json:"gameId"`
				} `json:"data"`
			} `json:"encounteredGames"`
			Tags []struct {
				SelfPuuid string `json:"selfPuuid"`
			} `json:"tags"`
		}
		data, err := json.Marshal(value)
		if err != nil || json.Unmarshal(data, &info) != nil {
			continue
		}
		for _, game := range info.EncounteredGames.Data {
			if game.GameID > 0 {
				games[game.GameID] = true
			}
		}
		for _, tag := range info.Tags {
			if validPUUID(tag.SelfPuuid) {
				authors[tag.SelfPuuid] = true
			}
		}
	}
	return games, authors
}

func (s *Service) loadAuxiliaryInfo(ctx context.Context) {
	games, authors := s.savedInfoReferences()
	s.mu.Lock()
	historyIDs := map[int64]bool{}
	for _, ids := range s.historyGameIDs {
		for _, id := range ids {
			historyIDs[id] = true
		}
	}
	for id := range client.Map(s.data["additionalGames"]) {
		number, _ := strconv.ParseInt(id, 10, 64)
		if !games[number] {
			delete(client.Map(s.data["additionalGames"]), id)
		}
	}
	var missing []int64
	for id := range games {
		if !historyIDs[id] && client.Map(s.data["additionalGames"])[strconv.FormatInt(id, 10)] == nil {
			missing = append(missing, id)
		}
	}
	s.mu.Unlock()
	var wg sync.WaitGroup
	gate := make(chan struct{}, s.config().concurrency)
	run := func(fn func()) {
		wg.Add(1)
		go func() {
			defer wg.Done()
			select {
			case gate <- struct{}{}:
			case <-ctx.Done():
				return
			}
			defer func() { <-gate }()
			fn()
		}()
	}
	for _, id := range missing {
		id := id
		run(func() { _ = s.loadAdditionalGame(ctx, id) })
	}
	for author := range authors {
		author := author
		run(func() { _ = s.loadMetadata(ctx, author, "summoner", false) })
	}
	wg.Wait()
}

func (s *Service) loadAdditionalGame(ctx context.Context, gameID int64) error {
	source := s.config().source
	sgp := fmt.Sprintf("/match-history-query/v1/products/lol/@akari:sgpServerSubId@_%d/SUMMARY", gameID)
	lcu := fmt.Sprintf("/lol-match-history/v1/games/%d", gameID)
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
		return err
	}
	encoded, err := json.Marshal(data)
	if err != nil {
		return err
	}
	if source == "sgp" {
		var envelope map[string]json.RawMessage
		if err := json.Unmarshal(encoded, &envelope); err != nil {
			return err
		}
		if len(envelope["json"]) == 0 || string(envelope["json"]) == "null" {
			return fmt.Errorf("SGP game %d has no summary", gameID)
		}
		compacted, _, err := stripUnusedMissions(envelope["json"])
		if err != nil {
			return err
		}
		envelope["json"] = compacted
		encoded, err = json.Marshal(envelope)
		if err != nil {
			return err
		}
	} else {
		encoded, _, err = stripUnusedMissions(encoded)
		if err != nil {
			return err
		}
	}
	wrapped, err := json.Marshal(summaryWrapper{Source: source, GameID: gameID, Data: encoded})
	if err != nil {
		return err
	}
	s.mu.Lock()
	client.Map(s.data["additionalGames"])[strconv.FormatInt(gameID, 10)] = json.RawMessage(wrapped)
	s.mu.Unlock()
	if s.emit != nil {
		s.emit("ongoing-game-main", "additional-game-loaded", gameID, json.RawMessage(wrapped))
	}
	return nil
}
