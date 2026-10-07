package game

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
)

func (s *Service) requestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	if backend, ok := s.backend.(interface {
		RequestScope(context.Context) (context.Context, context.CancelFunc)
	}); ok {
		return backend.RequestScope(ctx)
	}
	return ctx, func() {}
}

// LCU history lists can omit teammates and their statistics. Like the original
// loader, complete each game through /games/{id}, retaining the list row if a
// single game cannot be fetched. All players share the configured misc slots.
func (s *Service) completeLCUHistory(ctx context.Context, games []summaryWrapper) ([]summaryWrapper, error) {
	server, source := s.backend.CurrentServer(), s.config().source
	concurrency := max(1, s.config().concurrency)
	s.lcuSummaryMu.Lock()
	if s.lcuSummaryGate == nil || cap(s.lcuSummaryGate) != concurrency {
		s.lcuSummaryGate = make(chan struct{}, concurrency)
	}
	gate := s.lcuSummaryGate
	s.lcuSummaryMu.Unlock()
	var pending sync.WaitGroup
	for index := range games {
		pending.Add(1)
		go func(index int) {
			defer pending.Done()
			row := games[index]
			if row.GameID <= 0 {
				return
			}
			if complete, err := s.lcuGameSummary(ctx, server, source, row.GameID, gate); err == nil {
				games[index] = complete
			}
		}(index)
	}
	pending.Wait()
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if s.backend.CurrentServer() != server || s.config().source != source {
		return nil, context.Canceled
	}
	return games, nil
}

func (s *Service) lcuGameSummary(ctx context.Context, server, preferredSource string, gameID int64, gate chan struct{}) (summaryWrapper, error) {
	key := fmt.Sprintf("%s:%d", server, gameID)
	for {
		s.lcuSummaryMu.Lock()
		if cached, exists := s.lcuSummaries[key]; exists {
			s.touchLCUSummary(key)
			s.lcuSummaryMu.Unlock()
			return cached, nil
		}
		if done := s.lcuSummaryRequests[key]; done != nil {
			s.lcuSummaryMu.Unlock()
			select {
			case <-done:
				continue
			case <-ctx.Done():
				return summaryWrapper{}, ctx.Err()
			}
		}
		if s.lcuSummaryRequests == nil {
			s.lcuSummaryRequests = make(map[string]chan struct{})
		}
		done := make(chan struct{})
		s.lcuSummaryRequests[key] = done
		s.lcuSummaryMu.Unlock()
		defer func() { s.lcuSummaryMu.Lock(); delete(s.lcuSummaryRequests, key); close(done); s.lcuSummaryMu.Unlock() }()
		break
	}
	select {
	case gate <- struct{}{}:
	case <-ctx.Done():
		return summaryWrapper{}, ctx.Err()
	}
	defer func() { <-gate }()
	data, err := s.queryJSON(ctx, "lcu", "", fmt.Sprintf("/lol-match-history/v1/games/%d", gameID))
	if err != nil {
		return summaryWrapper{}, err
	}
	if err := ctx.Err(); err != nil {
		return summaryWrapper{}, err
	}
	if s.backend.CurrentServer() != server || s.config().source != preferredSource {
		return summaryWrapper{}, context.Canceled
	}
	encoded, err := json.Marshal(data)
	if err != nil {
		return summaryWrapper{}, err
	}
	compacted, id, err := stripUnusedMissions(encoded)
	if err != nil {
		return summaryWrapper{}, err
	}
	if id != gameID {
		return summaryWrapper{}, fmt.Errorf("LCU summary ID %d does not match requested game %d", id, gameID)
	}
	row := summaryWrapper{Source: "lcu", GameID: gameID, Data: compacted}
	s.lcuSummaryMu.Lock()
	if s.lcuSummaries == nil {
		s.lcuSummaries = make(map[string]summaryWrapper)
	}
	s.lcuSummaries[key] = row
	s.touchLCUSummary(key)
	if len(s.lcuSummaryOrder) > 256 {
		delete(s.lcuSummaries, s.lcuSummaryOrder[0])
		s.lcuSummaryOrder = s.lcuSummaryOrder[1:]
	}
	s.lcuSummaryMu.Unlock()
	return row, nil
}

// Called with lcuSummaryMu held; the original summary LRU also retains 256 games.
func (s *Service) touchLCUSummary(key string) {
	for index, existing := range s.lcuSummaryOrder {
		if existing == key {
			s.lcuSummaryOrder = append(s.lcuSummaryOrder[:index], s.lcuSummaryOrder[index+1:]...)
			break
		}
	}
	s.lcuSummaryOrder = append(s.lcuSummaryOrder, key)
}
