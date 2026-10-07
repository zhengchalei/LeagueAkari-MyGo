package game

import (
	"context"
	"fmt"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type queryLimitsBackend struct {
	*fakeBackend
	mu             sync.Mutex
	counts         []int
	details        map[string]int
	started        chan struct{}
	release        chan struct{}
	detailsStarted chan struct{}
	detailsRelease chan struct{}
}

func queryLimitsFixture() *queryLimitsBackend {
	return &queryLimitsBackend{fakeBackend: newFake(), details: map[string]int{}}
}

func (b *queryLimitsBackend) SGPJSON(ctx context.Context, server, token, method, path string, body any) (any, error) {
	if strings.Contains(path, "/SUMMARY?") {
		location, err := url.Parse(path)
		if err != nil {
			return nil, err
		}
		count, err := strconv.Atoi(location.Query().Get("count"))
		if err != nil {
			return nil, err
		}
		b.mu.Lock()
		b.counts = append(b.counts, count)
		b.mu.Unlock()
		if b.started != nil {
			b.started <- struct{}{}
			select {
			case <-b.release:
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		}
		games := make([]any, count)
		for i := range games {
			games[i] = map[string]any{"json": map[string]any{"gameId": i + 1, "participants": []any{}}}
		}
		return map[string]any{"games": games}, nil
	}
	if strings.HasSuffix(path, "/DETAILS") {
		b.mu.Lock()
		b.details[path]++
		b.mu.Unlock()
		if b.detailsStarted != nil {
			b.detailsStarted <- struct{}{}
			select {
			case <-b.detailsRelease:
			case <-ctx.Done():
				return nil, ctx.Err()
			}
		}
		return map[string]any{"json": map[string]any{"frames": []any{}}}, nil
	}
	return b.fakeBackend.SGPJSON(ctx, server, token, method, path, body)
}

func TestTwoHundredHistoriesAndDetailsReachTheDataSource(t *testing.T) {
	for _, configuredCount := range []int{200} {
		t.Run(strconv.Itoa(configuredCount), func(t *testing.T) {
			b := queryLimitsFixture()
			store := gameSettings(t)
			setGameSetting(t, store, "matchHistoryLoadCount", configuredCount)
			setGameSetting(t, store, "gameDetailsLoadCount", 200)
			s := New(b, nil)
			s.SetSettings(store)
			if err := s.Refresh(context.Background()); err != nil {
				t.Fatal(err)
			}
			for _, count := range b.counts {
				if count != 200 {
					t.Fatalf("data source requested %d summaries instead of 200", count)
				}
			}
			if len(b.counts) != 2 {
				t.Fatalf("expected both players, got %v", b.counts)
			}
			history := client.Map(decodedRaw(client.Map(s.GetAll()["matchHistory"])["self"]))
			if len(client.List(history["data"])) != 200 {
				t.Fatal("summary retention truncated the source response")
			}
			if len(b.details) != 200 || len(client.Map(s.GetAll()["gameDetails"])) != 200 {
				t.Fatalf("details were truncated: fetched %d retained %d", len(b.details), len(client.Map(s.GetAll()["gameDetails"])))
			}
			for path, count := range b.details {
				if count != 1 {
					t.Fatalf("shared timeline %s fetched %d times", path, count)
				}
			}
			// The original setter lowers the persisted detail count with the history count.
			setGameSetting(t, store, "matchHistoryLoadCount", 1)
			if err := s.Refresh(context.Background()); err != nil {
				t.Fatal(err)
			}
			if len(client.Map(s.GetAll()["gameDetails"])) != 1 {
				t.Fatal("details exceeded the current one-game history")
			}
			if client.Number(store.Get("ongoing-game-main", "gameDetailsLoadCount")) != 1 {
				t.Fatal("history setter did not persist the lower detail count")
			}
		})
	}
}

func TestConfiguredConcurrencyAboveSixteenStartsEverySlot(t *testing.T) {
	const concurrency = 24
	b := queryLimitsFixture()
	b.started = make(chan struct{}, concurrency)
	b.release = make(chan struct{})
	store := gameSettings(t)
	setGameSetting(t, store, "concurrency", concurrency)
	setGameSetting(t, store, "matchHistoryLoadCount", 1)
	s := New(b, nil)
	s.SetSettings(store)
	players := make([]any, concurrency)
	for i := range players {
		players[i] = fmt.Sprintf("arena-player-%d", i)
	}
	if err := s.SetDraft(map[string]any{"gameModeKind": "cherry", "queueId": 1700, "teams": map[string]any{"TEAM-ALL": players}}); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.Refresh(ctx) }()
	started := 0
	for started < concurrency {
		select {
		case <-b.started:
			started++
		case <-ctx.Done():
			close(b.release)
			<-done
			t.Fatalf("only %d of %d configured slots reached the data source", started, concurrency)
		}
	}
	close(b.release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if len(client.Map(s.GetAll()["matchHistory"])) != concurrency {
		t.Fatal("some players' history did not finish")
	}
}

func TestLoadCountSetterKeepsOriginalTwoHundredLimit(t *testing.T) {
	b := queryLimitsFixture()
	s := New(b, nil)
	s.SetMatchHistoryLoadCount(200)
	if err := s.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	for _, count := range b.counts {
		if count != 200 {
			t.Fatalf("setter reduced the real request to %d", count)
		}
	}
}
