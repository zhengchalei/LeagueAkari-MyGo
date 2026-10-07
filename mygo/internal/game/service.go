package game

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"reflect"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type Backend interface {
	State() map[string]any
	CurrentServer() string
	JSON(context.Context, string, string, any) (any, error)
	SGPJSON(context.Context, string, string, string, string, any) (any, error)
}

type Service struct {
	mu                  sync.RWMutex
	refreshMu           sync.Mutex
	timelineMu          sync.Mutex
	timelineRequests    map[string]chan struct{}
	lcuSummaryMu        sync.Mutex
	lcuSummaryRequests  map[string]chan struct{}
	lcuSummaries        map[string]summaryWrapper
	lcuSummaryOrder     []string
	lcuSummaryGate      chan struct{}
	backend             Backend
	emit                bridge.Emitter
	state               map[string]any
	data                map[string]any
	loaded              map[string]time.Time
	server              string
	timelineOrder       []string
	timelines           map[string]any
	lastChampSelect     map[string]any
	lastGameID          int64
	count               int
	tagParams           map[string]any
	historyScope        string
	playerStore         *player.Service
	disposePlayers      func()
	recordScope         string
	savedInfoScopes     map[string]string
	historyGameIDs      map[string][]int64
	rosterScope         string
	rosterFetchedAt     time.Time
	additionalRoster    map[string]any
	settings            *settings.Store
	preferredSource     string
	prefetchedTimelines map[string]bool
	reminderScope       string
	remindedPlayers     map[string]bool
	queueTagScope       string
}

func New(backend Backend, emit bridge.Emitter) *Service {
	return &Service{backend: backend, emit: emit, state: defaultState(), data: emptyData(), loaded: map[string]time.Time{}, timelines: map[string]any{}, count: 50, tagParams: map[string]any{}, savedInfoScopes: map[string]string{}, historyGameIDs: map[string][]int64{}, prefetchedTimelines: map[string]bool{}}
}

func emptyData() map[string]any {
	result := map[string]any{}
	for _, key := range []string{"matchHistory", "summoner", "rankedStats", "savedInfo", "championMastery", "gameDetails", "additionalGames"} {
		result[key] = map[string]any{}
	}
	return result
}

func defaultState() map[string]any {
	return map[string]any{"teams": map[string]any{}, "championSelections": map[string]any{}, "positionAssignments": map[string]any{}, "queryStage": map[string]any{"phase": "unavailable", "gameInfo": nil}, "analysis": nil, "isInEog": false, "matchHistoryLoadingState": map[string]any{}, "summonerLoadingState": map[string]any{}, "rankedStatsLoadingState": map[string]any{}, "savedInfoLoadingState": map[string]any{}, "championMasteryLoadingState": map[string]any{}, "teamParticipantGroups": map[string]any{}, "matchHistoryTagParams": map[string]any{}, "draft": nil, "additional": map[string]any{"teams": map[string]any{}, "selections": map[string]any{}, "positions": map[string]any{}, "spells": map[string]any{}, "teamParticipantGroups": map[string]any{}}, "mergedPremadeTeamMap": map[string]any{}, "inferredPremadeTeams": []any{}}
}

func (s *Service) State() map[string]any {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return client.Clone(s.state).(map[string]any)
}
func (s *Service) GetAll() map[string]any {
	s.mu.RLock()
	defer s.mu.RUnlock()
	// Entry values are immutable after publication; copy only mutable containers.
	result := map[string]any{}
	for kind, entries := range s.data {
		copy := map[string]any{}
		for key, value := range client.Map(entries) {
			copy[key] = value
		}
		result[kind] = copy
	}
	return result
}

func (s *Service) set(key string, value any) {
	s.mu.Lock()
	changed := !reflect.DeepEqual(s.state[key], value)
	s.state[key] = value
	s.mu.Unlock()
	if changed && s.emit != nil {
		s.emit("mobx-utils-main", "update-state-prop/ongoing-game-main:state", key, value, map[string]any{"action": "update", "raw": true})
	}
}

func (s *Service) loading(kind, puuid, value string) {
	s.mu.Lock()
	states := client.Map(s.state[kind+"LoadingState"])
	states[puuid] = value
	s.mu.Unlock()
	if s.emit != nil {
		s.emit("mobx-utils-main", "update-state-prop/ongoing-game-main:state", kind+"LoadingState."+puuid, value, map[string]any{"action": "update", "raw": true})
	}
}

func (s *Service) put(kind, event, puuid string, value any) {
	if _, ok := value.(json.RawMessage); !ok {
		data, err := json.Marshal(value)
		if err == nil {
			value = json.RawMessage(data)
		}
	}
	s.mu.Lock()
	client.Map(s.data[kind])[puuid] = value
	s.mu.Unlock()
	if s.emit != nil {
		s.emit("ongoing-game-main", event, puuid, value)
	}
}

func (s *Service) SetMatchHistoryTagParams(params map[string]any) {
	s.mu.Lock()
	s.tagParams = client.Clone(params).(map[string]any)
	s.loaded = map[string]time.Time{}
	s.mu.Unlock()
	s.set("matchHistoryTagParams", params)
}

func (s *Service) SetMatchHistoryLoadCount(count int) {
	if count < 1 {
		count = 1
	}
	if count > 200 {
		count = 200
	}
	s.mu.Lock()
	s.count = count
	s.loaded = map[string]time.Time{}
	s.mu.Unlock()
}

func (s *Service) Refresh(ctx context.Context) error {
	s.refreshMu.Lock()
	defer s.refreshMu.Unlock()
	ctx, release := s.requestScope(ctx)
	defer release()
	var view map[string]any
	if gameplay, ok := s.backend.(interface{ GameplayState() map[string]any }); ok {
		view = gameplay.GameplayState()
	} else {
		view = s.backend.State()
	}
	server := s.backend.CurrentServer()
	config := s.config()
	if !config.enabled {
		s.clearQueryData()
		s.set("queryStage", map[string]any{"phase": "unavailable", "gameInfo": nil})
		s.set("teams", map[string]any{})
		s.set("championSelections", map[string]any{})
		s.set("positionAssignments", map[string]any{})
		s.set("additional", emptyAdditional())
		s.set("teamParticipantGroups", map[string]any{})
		return nil
	}
	s.mu.RLock()
	serverChanged := s.server != server
	sourceChanged := s.preferredSource != "" && config.source != s.preferredSource
	s.mu.RUnlock()
	if serverChanged || sourceChanged {
		s.clearQueryData()
	}
	s.mu.Lock()
	if s.server != server {
		s.data = emptyData()
		s.loaded = map[string]time.Time{}
		s.timelines = map[string]any{}
		s.timelineOrder = nil
		s.lastChampSelect = nil
		s.savedInfoScopes = map[string]string{}
		s.historyGameIDs = map[string][]int64{}
		s.server = server
	}
	if config.source != s.preferredSource || config.count != s.count {
		s.loaded = map[string]time.Time{}
		s.preferredSource = config.source
		s.count = config.count
	}
	s.mu.Unlock()
	stage, teams, selections, positions := s.collect(ctx, view)
	if stage["phase"] == "lobby" && !config.queryLobby {
		stage = map[string]any{"phase": "unavailable", "gameInfo": nil}
		teams, selections, positions = map[string]any{}, map[string]any{}, map[string]any{}
	}
	s.syncQueueTag(view, stage, config)
	s.applyParticipantGroups(view)
	phase := client.String(client.Map(view["gameflow"])["phase"])
	scope := fmt.Sprintf("%s:%d:%t", client.String(stage["phase"]), client.Number(client.Map(stage["gameInfo"])["gameId"]), phase == "WaitingForStats" || phase == "PreEndOfGame" || phase == "EndOfGame")
	s.mu.Lock()
	if s.historyScope != scope {
		s.loaded = map[string]time.Time{}
		s.historyScope = scope
	}
	s.mu.Unlock()
	if stage["phase"] == "unavailable" {
		s.clearQueryData()
	}
	s.set("queryStage", stage)
	s.set("teams", teams)
	s.set("championSelections", selections)
	s.set("positionAssignments", positions)
	s.set("isInEog", phase == "WaitingForStats" || phase == "PreEndOfGame" || phase == "EndOfGame")
	players := map[string]bool{}
	for _, members := range teams {
		for _, member := range client.List(members) {
			players[client.String(member)] = true
		}
	}
	s.removeAbsent(players)
	if len(players) == 0 {
		s.mu.Lock()
		s.historyGameIDs = map[string][]int64{}
		s.savedInfoScopes = map[string]string{}
		s.mu.Unlock()
	}
	if err := s.recordEncounteredGame(view, stage, teams); err != nil {
		return err
	}
	for puuid := range players {
		s.loadSavedInfo(puuid, false)
	}
	ctx, finishPrefetch := s.startDetailsPrefetch(ctx, config.detailsCount, config.concurrency)
	defer finishPrefetch()
	var wg sync.WaitGroup
	gate := make(chan struct{}, config.concurrency)
	for puuid := range players {
		if puuid == "" {
			continue
		}
		wg.Add(1)
		go func(puuid string) {
			defer wg.Done()
			select {
			case gate <- struct{}{}:
			case <-ctx.Done():
				return
			}
			defer func() { <-gate }()
			_ = s.loadPlayer(ctx, puuid, false)
		}(puuid)
	}
	wg.Wait()
	s.loadAuxiliaryInfo(ctx)
	s.remindTaggedPlayers(ctx)
	return ctx.Err()
}

func (s *Service) Reload(ctx context.Context) error {
	s.mu.Lock()
	s.loaded = map[string]time.Time{}
	s.savedInfoScopes = map[string]string{}
	s.mu.Unlock()
	return s.Refresh(ctx)
}
func (s *Service) ReloadPlayer(ctx context.Context, puuid string) error {
	return s.ReloadPlayerWithOptions(ctx, puuid, nil)
}

func (s *Service) removeAbsent(players map[string]bool) {
	_, authors := s.savedInfoReferences()
	events := map[string]string{"summoner": "summoner-removed", "rankedStats": "ranked-stats-removed", "championMastery": "champion-mastery-removed", "matchHistory": "match-history-removed", "savedInfo": "saved-info-removed"}
	for kind, event := range events {
		var removed []string
		s.mu.Lock()
		for puuid := range client.Map(s.data[kind]) {
			if !players[puuid] {
				if kind == "summoner" && authors[puuid] {
					continue
				}
				delete(client.Map(s.data[kind]), puuid)
				delete(s.loaded, puuid)
				delete(s.savedInfoScopes, puuid)
				delete(s.historyGameIDs, puuid)
				delete(client.Map(s.state[kind+"LoadingState"]), puuid)
				removed = append(removed, puuid)
			}
		}
		s.mu.Unlock()
		if s.emit != nil {
			for _, puuid := range removed {
				s.emit("ongoing-game-main", event, puuid)
			}
		}
	}
}

func (s *Service) loadPlayer(ctx context.Context, puuid string, force bool) error {
	return s.loadScopes(ctx, puuid, playerReloadScopes, force)
}

func (s *Service) loadMatchHistory(ctx context.Context, puuid string, force bool) error {
	s.mu.RLock()
	loaded := s.loaded[puuid]
	count := s.count
	params := client.Clone(s.tagParams).(map[string]any)
	activeGame := client.String(client.Map(s.state["queryStage"])["phase"]) == "in-game"
	s.mu.RUnlock()
	// Histories cannot change during a match. Refresh on the next phase or an
	// explicit user reload, rather than decoding all ten players every minute.
	if !force && !loaded.IsZero() && (activeGame || time.Since(loaded) < time.Minute) {
		return nil
	}
	s.loading("matchHistory", puuid, "loading")
	params["startIndex"] = 0
	params["count"] = count
	query := url.Values{}
	query.Set("startIndex", "0")
	query.Set("count", strconv.Itoa(count))
	for _, key := range []string{"tag", "tagsQueryType"} {
		if value := client.String(params[key]); value != "" {
			query.Set(key, value)
		}
	}
	games, source, err := s.history(ctx, puuid, count, query)
	if err != nil {
		if ctx.Err() == nil {
			s.loading("matchHistory", puuid, "error")
		}
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	// Only summaries are retained. Timeline DETAILS must be explicitly requested.
	s.put("matchHistory", "match-history-loaded", puuid, map[string]any{"source": source, "params": params, "data": games})
	s.loading("matchHistory", puuid, "loaded")
	s.mu.Lock()
	s.loaded[puuid] = time.Now()
	if summaries, ok := games.([]summaryWrapper); ok {
		ids := make([]int64, 0, len(summaries))
		for _, summary := range summaries {
			ids = append(ids, summary.GameID)
		}
		s.historyGameIDs[puuid] = ids
	}
	s.mu.Unlock()
	notifyPrefetch(ctx)
	return nil
}

func (s *Service) GetTimeline(ctx context.Context, server string, gameID int64) (any, error) {
	ctx, release := s.requestScope(ctx)
	defer release()
	if server == "" {
		server = s.backend.CurrentServer()
	}
	key := server + ":" + strconv.FormatInt(gameID, 10)
	// Coalesce requests for the same game without serializing unrelated games.
	for {
		s.timelineMu.Lock()
		if done := s.timelineRequests[key]; done != nil {
			s.timelineMu.Unlock()
			select {
			case <-done:
				continue
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		}
		if s.timelineRequests == nil {
			s.timelineRequests = make(map[string]chan struct{})
		}
		done := make(chan struct{})
		s.timelineRequests[key] = done
		s.timelineMu.Unlock()
		defer func() {
			s.timelineMu.Lock()
			delete(s.timelineRequests, key)
			close(done)
			s.timelineMu.Unlock()
		}()
		break
	}
	s.mu.RLock()
	cached, exists := s.timelines[key]
	s.mu.RUnlock()
	if exists {
		return cached, nil
	}
	data, source, err := s.timeline(ctx, server, gameID)
	if err != nil {
		return nil, err
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	encoded, err := json.Marshal(map[string]any{"source": source, "gameId": gameID, "data": data})
	if err != nil {
		return nil, err
	}
	wrapped := json.RawMessage(encoded)
	s.mu.Lock()
	s.timelines[key] = wrapped
	if !s.prefetchedTimelines[key] {
		s.timelineOrder = append(s.timelineOrder, key)
	}
	var removedID string
	if len(s.timelineOrder) > 4 {
		old := s.timelineOrder[0]
		s.timelineOrder = s.timelineOrder[1:]
		if !s.prefetchedTimelines[old] {
			delete(s.timelines, old)
			removedID = strings.Split(old, ":")[1]
			delete(client.Map(s.data["gameDetails"]), removedID)
		}
	}
	client.Map(s.data["gameDetails"])[strconv.FormatInt(gameID, 10)] = wrapped
	s.mu.Unlock()
	if s.emit != nil {
		if removedID != "" {
			id, _ := strconv.ParseInt(removedID, 10, 64)
			s.emit("ongoing-game-main", "game-details-removed", id)
		}
		s.emit("ongoing-game-main", "game-details-loaded", gameID, wrapped)
	}
	return wrapped, nil
}
