package game

import (
	"context"
	"fmt"
	"net/http"
	"strconv"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func (s *Service) queryJSON(ctx context.Context, source, server, path string) (any, error) {
	if raw, ok := s.backend.(rawBackend); ok {
		if source == "sgp" {
			return raw.SGPJSONRaw(ctx, server, "entitlements", http.MethodGet, path, nil)
		}
		return raw.JSONRaw(ctx, http.MethodGet, path, nil)
	}
	if source == "sgp" {
		return s.backend.SGPJSON(ctx, server, "entitlements", http.MethodGet, path, nil)
	}
	return s.backend.JSON(ctx, http.MethodGet, path, nil)
}

func (s *Service) timeline(ctx context.Context, server string, gameID int64) (any, string, error) {
	source := s.config().source
	if server != s.backend.CurrentServer() {
		source = "sgp"
	}
	sgp := fmt.Sprintf("/match-history-query/v1/products/lol/@akari:sgpServerSubId@_%d/DETAILS", gameID)
	lcu := fmt.Sprintf("/lol-match-history/v1/game-timelines/%d", gameID)
	path := sgp
	if source == "lcu" {
		path = lcu
	}
	data, err := s.queryJSON(ctx, source, server, path)
	if err == nil || server != s.backend.CurrentServer() {
		return data, source, err
	}
	if source == "sgp" {
		source, path = "lcu", lcu
	} else {
		source, path = "sgp", sgp
	}
	data, err = s.queryJSON(ctx, source, server, path)
	return data, source, err
}

func (s *Service) syncPrefetchedDetails(ctx context.Context, count, concurrency int) {
	server := s.backend.CurrentServer()
	wanted := map[string]bool{}
	s.mu.Lock()
	for _, ids := range s.historyGameIDs {
		for _, id := range ids[:min(count, len(ids))] {
			wanted[server+":"+strconv.FormatInt(id, 10)] = true
		}
	}
	manual := map[string]bool{}
	for _, key := range s.timelineOrder {
		manual[key] = true
	}
	removed := []int64{}
	for key := range s.prefetchedTimelines {
		if !wanted[key] && !manual[key] {
			if value := s.timelines[key]; value != nil {
				id := client.Number(client.Map(decodedRaw(value))["gameId"])
				delete(client.Map(s.data["gameDetails"]), strconv.FormatInt(id, 10))
				removed = append(removed, id)
			}
			delete(s.timelines, key)
		}
	}
	s.prefetchedTimelines = wanted
	missing := []int64{}
	for key := range wanted {
		if s.timelines[key] == nil {
			id, _ := strconv.ParseInt(key[len(server)+1:], 10, 64)
			missing = append(missing, id)
		}
	}
	s.mu.Unlock()
	if s.emit != nil {
		for _, id := range removed {
			s.emit("ongoing-game-main", "game-details-removed", id)
		}
	}
	// The shared timeline lock serializes expensive DETAILS decodes. Each ID is
	// fetched once; default zero never schedules this work.
	for _, id := range missing {
		if ctx.Err() != nil {
			return
		}
		_, _ = s.GetTimeline(ctx, server, id)
	}
}
