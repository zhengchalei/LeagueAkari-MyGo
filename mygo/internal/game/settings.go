package game

import (
	"fmt"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type queryConfig struct {
	enabled, queryLobby              bool
	concurrency, count, detailsCount int
	source                           string
	tagPreference                    string
}

func (s *Service) SetSettings(store *settings.Store) {
	s.mu.Lock()
	s.settings = store
	s.mu.Unlock()
}

func (s *Service) config() queryConfig {
	s.mu.RLock()
	store, count := s.settings, s.count
	s.mu.RUnlock()
	config := queryConfig{enabled: true, queryLobby: true, concurrency: 3, count: count, source: "sgp", tagPreference: "current"}
	if store == nil {
		return config
	}
	values := store.Snapshot("ongoing-game-main")
	if enabled, ok := values["enabled"].(bool); ok {
		config.enabled = enabled
	}
	if enabled, ok := values["queryInLobbyPhase"].(bool); ok {
		config.queryLobby = enabled
	}
	if value := client.Number(values["concurrency"]); value > 0 {
		config.concurrency = min(int(value), 16)
	}
	if value := client.Number(values["matchHistoryLoadCount"]); value > 0 {
		config.count = min(int(value), 50)
	}
	config.detailsCount = max(0, min(int(client.Number(values["gameDetailsLoadCount"])), config.count))
	if store.Get("app-common-main", "preferredLolSource") == "lcu" {
		config.source = "lcu"
	}
	if values["matchHistoryTagPreference"] == "all" {
		config.tagPreference = "all"
	}
	return config
}

func (s *Service) syncQueueTag(view, stage map[string]any, config queryConfig) {
	queueID := client.Number(client.Map(stage["gameInfo"])["queueId"])
	if stage["phase"] != "draft" {
		lobby := client.Map(client.Map(view["lobby"])["lobby"])
		if len(lobby) > 0 {
			queueID = client.Number(client.Map(lobby["gameConfig"])["queueId"])
		}
	}
	scope := fmt.Sprintf("%s:%d", config.tagPreference, queueID)
	s.mu.Lock()
	if s.queueTagScope == scope {
		s.mu.Unlock()
		return
	}
	s.queueTagScope = scope
	params := client.Clone(s.tagParams).(map[string]any)
	if config.tagPreference == "current" && queueID > 0 {
		params["tag"] = fmt.Sprintf("q_%d", queueID)
	} else {
		delete(params, "tag")
	}
	s.tagParams = params
	s.loaded = map[string]time.Time{}
	s.mu.Unlock()
	s.set("matchHistoryTagParams", params)
}

func (s *Service) clearQueryData() {
	s.mu.Lock()
	hadData := false
	for _, value := range s.data {
		if len(client.Map(value)) > 0 {
			hadData = true
			break
		}
	}
	s.data = emptyData()
	s.loaded = map[string]time.Time{}
	s.savedInfoScopes = map[string]string{}
	s.historyGameIDs = map[string][]int64{}
	s.timelines = map[string]any{}
	s.timelineOrder = nil
	s.prefetchedTimelines = map[string]bool{}
	s.additionalRoster = nil
	s.rosterScope = ""
	s.mu.Unlock()
	for _, kind := range []string{"matchHistory", "summoner", "rankedStats", "savedInfo", "championMastery"} {
		s.set(kind+"LoadingState", map[string]any{})
	}
	if hadData && s.emit != nil {
		s.emit("ongoing-game-main", "clear")
	}
}
