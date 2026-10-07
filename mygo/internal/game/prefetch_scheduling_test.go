package game

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestDetailsArriveBeforeSlowHistoryWithSharedConcurrencyAndDedup(t *testing.T) {
	slowHistory, releaseHistory := make(chan struct{}, 1), make(chan struct{})
	detailStarted, releaseDetails := make(chan int, 8), make(chan struct{})
	var mu sync.Mutex
	requests, active, maximum := map[int]int{}, 0, 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		if strings.HasSuffix(path, "/matches") {
			ids := []int{1, 2, 3}
			if strings.Contains(path, "/teammate/") {
				slowHistory <- struct{}{}
				select {
				case <-releaseHistory:
				case <-r.Context().Done():
					return
				}
				ids = []int{2, 4, 5}
			}
			games := []any{}
			for _, id := range ids {
				games = append(games, map[string]any{"gameId": id})
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"games": map[string]any{"games": games}})
			return
		}
		if strings.Contains(path, "/games/") {
			id, _ := strconv.Atoi(path[strings.LastIndex(path, "/")+1:])
			_ = json.NewEncoder(w).Encode(map[string]any{"gameId": id, "participants": []any{}})
			return
		}
		if strings.Contains(path, "/game-timelines/") {
			id, _ := strconv.Atoi(path[strings.LastIndex(path, "/")+1:])
			mu.Lock()
			requests[id]++
			active++
			maximum = max(maximum, active)
			mu.Unlock()
			detailStarted <- id
			select {
			case <-releaseDetails:
			case <-r.Context().Done():
			}
			mu.Lock()
			active--
			mu.Unlock()
			_ = json.NewEncoder(w).Encode(map[string]any{"frames": []any{}})
			return
		}
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	s, _ := lcuHistoryService(t, server, 3, 2)
	setGameSetting(t, s.settings, "gameDetailsLoadCount", 2)
	historyLoaded := make(chan time.Time, 1)
	s.emit = func(namespace, event string, args ...any) {
		if namespace == "ongoing-game-main" && event == "match-history-loaded" && args[0] == "self" {
			historyLoaded <- time.Now()
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.Refresh(ctx) }()
	select {
	case <-slowHistory:
	case <-ctx.Done():
		t.Fatal("slow history not requested")
	}
	seen := map[int]bool{}
	var arrival time.Time
	select {
	case arrival = <-historyLoaded:
	case <-ctx.Done():
		close(releaseHistory)
		close(releaseDetails)
		<-done
		t.Fatal("first history not published")
	}
	for len(seen) < 2 {
		select {
		case id := <-detailStarted:
			if time.Since(arrival) < 250*time.Millisecond {
				t.Fatal("details skipped original delayed reaction")
			}
			seen[id] = true
		case <-ctx.Done():
			close(releaseHistory)
			close(releaseDetails)
			<-done
			t.Fatal("details waited for unrelated slow history")
		}
	}
	if !seen[1] || !seen[2] {
		t.Fatal("detail limit was ignored", seen)
	}
	// The second history arrives while the first reaction still has HTTP work.
	close(releaseHistory)
	deadline := time.After(time.Second)
	for {
		if len(client.Map(s.GetAll()["matchHistory"])) == 2 {
			break
		}
		select {
		case <-deadline:
			close(releaseDetails)
			<-done
			t.Fatal("second history not published")
		case <-time.After(time.Millisecond):
		}
	}
	// More than a reaction window: a separate per-window gate would overrun.
	select {
	case id := <-detailStarted:
		close(releaseDetails)
		<-done
		t.Fatalf("second reaction exceeded shared slots: %d", id)
	case <-time.After(400 * time.Millisecond):
	}
	close(releaseDetails)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	if maximum != 2 || len(requests) != 3 || requests[1] != 1 || requests[2] != 1 || requests[4] != 1 {
		t.Fatalf("concurrency/dedup/first-two limits changed: maximum=%d requests=%v", maximum, requests)
	}
	if len(client.Map(s.GetAll()["gameDetails"])) != 3 {
		t.Fatal("late player's details were not retained")
	}
}

func TestEarlyPrefetchCannotPublishAfterDisconnectOrPIDSwitch(t *testing.T) {
	for _, action := range []string{"disconnect", "switch"} {
		t.Run(action, func(t *testing.T) {
			started, release := make(chan struct{}, 1), make(chan struct{})
			old := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/matches") {
					_, _ = fmt.Fprint(w, `{"games":{"games":[{"gameId":1}]}}`)
					return
				}
				if strings.Contains(r.URL.Path, "/games/") {
					_, _ = fmt.Fprint(w, `{"gameId":1,"participants":[]}`)
					return
				}
				if strings.Contains(r.URL.Path, "/game-timelines/") {
					started <- struct{}{}
					<-release
					_, _ = fmt.Fprint(w, `{"frames":[],"owner":"old"}`)
					return
				}
				_, _ = fmt.Fprint(w, `{}`)
			}))
			defer old.Close()
			fresh := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/matches") {
					_, _ = fmt.Fprint(w, `{"games":{"games":[{"gameId":2}]}}`)
					return
				}
				if strings.Contains(r.URL.Path, "/games/") {
					_, _ = fmt.Fprint(w, `{"gameId":2,"participants":[]}`)
					return
				}
				_, _ = fmt.Fprint(w, `{"frames":[],"owner":"new"}`)
			}))
			defer fresh.Close()
			s, backend := lcuHistoryService(t, old, 1, 2)
			setGameSetting(t, s.settings, "gameDetailsLoadCount", 1)
			done := make(chan error, 1)
			go func() { done <- s.Refresh(context.Background()) }()
			select {
			case <-started:
			case <-time.After(2 * time.Second):
				t.Fatal("early timeline not requested")
			}
			if action == "disconnect" {
				backend.lc.Disconnect()
			} else {
				backend.lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL, Region: "TENCENT", PlatformID: "NJ100"})
			}
			close(release)
			select {
			case <-done:
			case <-time.After(time.Second):
				t.Fatal("old prefetch did not stop")
			}
			if len(client.Map(s.GetAll()["gameDetails"])) != 0 {
				t.Fatal("old timeline published after connection cancellation")
			}
			if action == "disconnect" {
				backend.lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL, Region: "TENCENT", PlatformID: "NJ100"})
			}
			if err := s.Reload(context.Background()); err != nil {
				t.Fatal(err)
			}
			details := client.Map(s.GetAll()["gameDetails"])
			if len(details) != 1 || client.String(client.Map(client.Map(decodedRaw(details["2"]))["data"])["owner"]) != "new" {
				t.Fatal("new client details missing or stale", details)
			}
		})
	}
}

func TestSinglePlayerHistoryReloadStartsSameDetailsReaction(t *testing.T) {
	var mu sync.Mutex
	requested := []int{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, "/matches") {
			_, _ = fmt.Fprint(w, `{"games":{"games":[{"gameId":101},{"gameId":102}]}}`)
			return
		}
		if strings.Contains(r.URL.Path, "/games/") {
			id, _ := strconv.Atoi(r.URL.Path[strings.LastIndex(r.URL.Path, "/")+1:])
			_, _ = fmt.Fprintf(w, `{"gameId":%d,"participants":[]}`, id)
			return
		}
		if strings.Contains(r.URL.Path, "/game-timelines/") {
			id, _ := strconv.Atoi(r.URL.Path[strings.LastIndex(r.URL.Path, "/")+1:])
			mu.Lock()
			requested = append(requested, id)
			mu.Unlock()
			_, _ = fmt.Fprint(w, `{"frames":[]}`)
			return
		}
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	s, _ := lcuHistoryService(t, server, 2, 2)
	setGameSetting(t, s.settings, "gameDetailsLoadCount", 1)
	if err := s.ReloadPlayerWithOptions(context.Background(), "self", map[string]any{"includes": []any{"matchHistory"}}); err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(requested) != 1 || requested[0] != 101 || len(client.Map(s.GetAll()["gameDetails"])) != 1 {
		t.Fatalf("single-player reload did not prefetch selected detail: %v", requested)
	}
}
