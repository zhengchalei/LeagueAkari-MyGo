package game

import (
	"context"
	"fmt"
	"net/http"
	"strconv"
	"sync"
	"time"

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

type prefetchNotificationKey struct{}

func notifyPrefetch(ctx context.Context) {
	if notify, ok := ctx.Value(prefetchNotificationKey{}).(func()); ok {
		notify()
	}
}

// MobX reaction's delay batches changes from the first arrival; later arrivals
// do not postpone that window or wait for all players' histories to finish.
func (s *Service) startDetailsPrefetch(ctx context.Context, count, concurrency int) (context.Context, func()) {
	changed, finished, done := make(chan struct{}, 1), make(chan struct{}), make(chan struct{})
	ready := make(chan struct{})
	finishSignal := finished
	notify := func() {
		select {
		case changed <- struct{}{}:
		default:
		}
	}
	go func() {
		defer close(done)
		gate := make(chan struct{}, max(1, concurrency))
		var pending sync.WaitGroup
		var mu sync.Mutex
		inFlight := map[int64]bool{}
		var timer *time.Timer
		var tick <-chan time.Time
		finishing := false
		flush := func() {
			if ctx.Err() != nil {
				return
			}
			server, missing := s.prefetchCandidates(count)
			for _, id := range missing {
				mu.Lock()
				if inFlight[id] {
					mu.Unlock()
					continue
				}
				inFlight[id] = true
				mu.Unlock()
				pending.Add(1)
				go func(id int64) {
					defer pending.Done()
					defer func() { mu.Lock(); delete(inFlight, id); mu.Unlock() }()
					select {
					case gate <- struct{}{}:
					case <-ctx.Done():
						return
					}
					defer func() { <-gate }()
					_, _ = s.GetTimeline(ctx, server, id)
				}(id)
			}
		}
		flush()
		close(ready)
		for {
			select {
			case <-changed:
				if tick == nil {
					timer = time.NewTimer(300 * time.Millisecond)
					tick = timer.C
				}
			case <-tick:
				tick = nil
				flush()
				if finishing {
					pending.Wait()
					return
				}
			case <-finished:
				finished = nil
				finishing = true
				// Histories finished before their notification was consumed.
				if tick == nil {
					select {
					case <-changed:
						timer = time.NewTimer(300 * time.Millisecond)
						tick = timer.C
					default:
						pending.Wait()
						return
					}
				}
			case <-ctx.Done():
				if timer != nil {
					timer.Stop()
				}
				pending.Wait()
				return
			}
		}
	}()
	<-ready
	return context.WithValue(ctx, prefetchNotificationKey{}, notify), func() { close(finishSignal); <-done }
}

func (s *Service) prefetchCandidates(count int) (string, []int64) {
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
	return server, missing
}
