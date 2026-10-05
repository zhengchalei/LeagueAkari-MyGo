package game

import (
	"fmt"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
)

func (s *Service) SetPlayerStore(store *player.Service) {
	s.mu.Lock()
	previous := s.disposePlayers
	s.playerStore = store
	s.disposePlayers = nil
	s.mu.Unlock()
	if previous != nil {
		previous()
	}
	if store != nil {
		cancel := store.OnChange(func(puuid, self string) { s.loadSavedInfo(puuid, true) })
		s.mu.Lock()
		s.disposePlayers = cancel
		s.mu.Unlock()
	}
}
func (s *Service) Close() {
	s.mu.Lock()
	cancel := s.disposePlayers
	s.disposePlayers = nil
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
}

func (s *Service) playerIdentity(view map[string]any) (string, string, string) {
	self := client.String(client.Map(client.Map(view["summoner"])["me"])["puuid"])
	auth := client.Map(client.Map(view["state"])["auth"])
	region := client.String(auth["region"])
	platform := client.String(auth["rsoPlatformId"])
	if region == "" {
		server := s.backend.CurrentServer()
		if strings.HasPrefix(server, "TENCENT_") {
			region = "TENCENT"
			platform = strings.TrimPrefix(server, "TENCENT_")
		} else {
			region = server
		}
	}
	return self, region, platform
}

func (s *Service) loadSavedInfo(puuid string, force bool) {
	s.mu.RLock()
	store := s.playerStore
	teams := client.Clone(s.state["teams"])
	s.mu.RUnlock()
	if store == nil {
		return
	}
	active := false
	for _, members := range client.Map(teams) {
		for _, member := range client.List(members) {
			if member == puuid {
				active = true
			}
		}
	}
	if !active {
		return
	}
	self, region, platform := s.playerIdentity(s.backend.State())
	if self == "" {
		return
	}
	identity := self + ":" + region + ":" + platform
	s.mu.RLock()
	previous := s.savedInfoScopes[puuid]
	s.mu.RUnlock()
	if !force && previous == identity {
		return
	}
	s.loading("savedInfo", puuid, "loading")
	info, err := store.QuerySavedPlayerWithGames(player.Query{Puuid: puuid, SelfPuuid: self, Region: &region, RsoPlatformID: &platform})
	if err != nil {
		s.loading("savedInfo", puuid, "error")
		return
	}
	if info != nil {
		s.put("savedInfo", "saved-info-loaded", puuid, info)
	} else {
		s.mu.Lock()
		_, existed := client.Map(s.data["savedInfo"])[puuid]
		delete(client.Map(s.data["savedInfo"]), puuid)
		s.mu.Unlock()
		if existed && s.emit != nil {
			s.emit("ongoing-game-main", "saved-info-removed", puuid)
		}
	}
	s.mu.Lock()
	s.savedInfoScopes[puuid] = identity
	s.mu.Unlock()
	s.loading("savedInfo", puuid, "loaded")
}

func (s *Service) recordEncounteredGame(view, stage, teams map[string]any) error {
	phase := client.String(client.Map(view["gameflow"])["phase"])
	if phase != "PreEndOfGame" && phase != "EndOfGame" {
		return nil
	}
	s.mu.RLock()
	store := s.playerStore
	draft := s.state["draft"]
	scope := s.recordScope
	s.mu.RUnlock()
	if store == nil || draft != nil {
		return nil
	}
	info := client.Map(stage["gameInfo"])
	gameID := client.Number(info["gameId"])
	if gameID <= 0 || client.String(stage["phase"]) != "in-game" {
		return nil
	}
	self, region, platform := s.playerIdentity(view)
	if self == "" || region == "" {
		return nil
	}
	currentScope := fmt.Sprintf("%s:%s:%s:%d", self, region, platform, gameID)
	if currentScope == scope {
		return nil
	}
	players := map[string]bool{}
	for _, members := range teams {
		for _, member := range client.List(members) {
			players[client.String(member)] = true
		}
	}
	if !players[self] {
		return nil
	}
	for puuid := range players {
		if puuid == self || !validPUUID(puuid) {
			continue
		}
		if _, err := store.RecordEncounter(player.Encounter{GameID: gameID, Puuid: puuid, SelfPuuid: self, Region: region, RsoPlatformID: platform, QueueType: client.String(info["queueType"])}); err != nil {
			return err
		}
	}
	s.mu.Lock()
	s.recordScope = currentScope
	s.mu.Unlock()
	return nil
}
