package game

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestDetailsPrefetchUsesConfiguredConcurrencyAboveSixteen(t *testing.T) {
	const concurrency = 24
	b := queryLimitsFixture()
	b.detailsStarted = make(chan struct{}, concurrency)
	b.detailsRelease = make(chan struct{})
	store := gameSettings(t)
	setGameSetting(t, store, "concurrency", concurrency)
	setGameSetting(t, store, "matchHistoryLoadCount", concurrency)
	setGameSetting(t, store, "gameDetailsLoadCount", concurrency)
	s := New(b, nil)
	s.SetSettings(store)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.Refresh(ctx) }()
	started := 0
	for started < concurrency {
		select {
		case <-b.detailsStarted:
			started++
		case <-ctx.Done():
			close(b.detailsRelease)
			<-done
			t.Fatalf("only %d of %d detail requests used the configured slots", started, concurrency)
		}
	}
	close(b.detailsRelease)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if len(client.Map(s.GetAll()["gameDetails"])) != concurrency {
		t.Fatal("some prefetched details were discarded")
	}
	for path, count := range b.details {
		if count != 1 {
			t.Fatalf("shared game %s fetched %d times", path, count)
		}
	}
}

func TestTimelineWaiterCancellationDoesNotCancelSharedRequest(t *testing.T) {
	b := queryLimitsFixture()
	b.detailsStarted = make(chan struct{}, 2)
	b.detailsRelease = make(chan struct{})
	s := New(b, nil)
	first := make(chan error, 1)
	go func() { _, err := s.GetTimeline(context.Background(), "", 777); first <- err }()
	select {
	case <-b.detailsStarted:
	case <-time.After(time.Second):
		t.Fatal("first timeline did not start")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Millisecond)
	defer cancel()
	if _, err := s.GetTimeline(ctx, "", 777); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("waiter did not cancel: %v", err)
	}
	select {
	case <-b.detailsStarted:
		t.Fatal("same game was fetched twice")
	default:
	}
	close(b.detailsRelease)
	if err := <-first; err != nil {
		t.Fatal(err)
	}
	if _, err := s.GetTimeline(context.Background(), "", 777); err != nil {
		t.Fatal(err)
	}
	if len(b.details) != 1 {
		t.Fatal("cached shared response was lost")
	}
	for _, count := range b.details {
		if count != 1 {
			t.Fatal("cached response was refetched")
		}
	}
}
