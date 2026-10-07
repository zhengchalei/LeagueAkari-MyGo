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

type lcuHTTPBackend struct {
	*fakeBackend
	lc *client.Client
}

func (b *lcuHTTPBackend) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return b.lc.JSON(ctx, method, path, body)
}
func (b *lcuHTTPBackend) RequestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	return b.lc.RequestScope(ctx)
}

func lcuHistoryService(t *testing.T, server *httptest.Server, count, concurrency int) (*Service, *lcuHTTPBackend) {
	t.Helper()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL, Region: "TENCENT", PlatformID: "NJ100"})
	backend := &lcuHTTPBackend{fakeBackend: newFake(), lc: lc}
	store := gameSettings(t)
	if err := store.Set("app-common-main", "preferredLolSource", "lcu"); err != nil {
		t.Fatal(err)
	}
	setGameSetting(t, store, "matchHistoryLoadCount", count)
	setGameSetting(t, store, "concurrency", concurrency)
	s := New(backend, nil)
	s.SetSettings(store)
	return s, backend
}

func TestLCUHistoryCompletesAllParticipantsUsingSharedConfiguredSlots(t *testing.T) {
	const count = 24
	started, release := make(chan int, count), make(chan struct{})
	var mu sync.Mutex
	requests := map[int]int{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, "/matches") {
			if r.URL.Query().Get("endIndex") != "23" {
				t.Errorf("LCU range ignored configured count: %s", r.URL.RawQuery)
			}
			games := make([]any, count)
			for i := range games {
				games[i] = map[string]any{"gameId": i + 1, "participants": []any{map[string]any{"participantId": 1}}, "fromList": true}
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"games": map[string]any{"games": games}})
			return
		}
		if strings.HasPrefix(r.URL.Path, "/lol-match-history/v1/games/") {
			id, _ := strconv.Atoi(strings.TrimPrefix(r.URL.Path, "/lol-match-history/v1/games/"))
			mu.Lock()
			requests[id]++
			mu.Unlock()
			started <- id
			select {
			case <-release:
			case <-r.Context().Done():
				return
			}
			if id == count {
				http.Error(w, "fixture unavailable", http.StatusInternalServerError)
				return
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"gameId": id, "participants": []any{map[string]any{"participantId": 1, "stats": map[string]any{"kills": 9, "totalDamageDealtToChampions": 12345}, "challenges": map[string]any{"soloKills": 3}}, map[string]any{"participantId": 2, "stats": map[string]any{"kills": 2}}}, "participantIdentities": []any{map[string]any{"participantId": 1, "player": map[string]any{"puuid": "self"}}, map[string]any{"participantId": 2, "player": map[string]any{"puuid": "ally"}}}})
			return
		}
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	s, _ := lcuHistoryService(t, server, count, count)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- s.Refresh(ctx) }()
	for reached := 0; reached < count; reached++ {
		select {
		case <-started:
		case <-ctx.Done():
			close(release)
			<-done
			t.Fatalf("only %d of 24 shared completion slots reached the HTTP server", reached)
		}
	}
	close(release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	history := client.Map(decodedRaw(client.Map(s.GetAll()["matchHistory"])["self"]))
	rows := client.List(history["data"])
	if len(rows) != count {
		t.Fatal("LCU history rows were lost")
	}
	for index, value := range rows {
		row := client.Map(value)
		data := client.Map(row["data"])
		if client.Number(row["gameId"]) != int64(index+1) {
			t.Fatal("completion changed list order")
		}
		if index == count-1 {
			if data["fromList"] != true {
				t.Fatal("failed completion did not retain original list row")
			}
			continue
		}
		participants := client.List(data["participants"])
		if len(participants) != 2 || client.Number(client.Map(client.Map(participants[0])["challenges"])["soloKills"]) != 3 || client.Number(client.Map(client.Map(participants[0])["stats"])["totalDamageDealtToChampions"]) != 12345 {
			t.Fatal("complete teammate statistics/challenges were not retained", data)
		}
	}
	mu.Lock()
	defer mu.Unlock()
	for id := 1; id < count; id++ {
		if requests[id] != 1 {
			t.Fatalf("shared game %d requested %d times", id, requests[id])
		}
	}
}

func TestOldLCUCompletionCannotPublishAfterDisconnectOrClientSwitch(t *testing.T) {
	for _, action := range []string{"disconnect", "switch"} {
		t.Run(action, func(t *testing.T) {
			started, release := make(chan struct{}, 1), make(chan struct{})
			old := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasSuffix(r.URL.Path, "/matches") {
					_, _ = fmt.Fprint(w, `{"games":{"games":[{"gameId":1}]}}`)
					return
				}
				if strings.HasSuffix(r.URL.Path, "/games/1") {
					started <- struct{}{}
					<-release
					_, _ = fmt.Fprint(w, `{"gameId":1,"owner":"old","participants":[]}`)
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
				_, _ = fmt.Fprint(w, `{"gameId":2,"owner":"new","participants":[]}`)
			}))
			defer fresh.Close()
			s, backend := lcuHistoryService(t, old, 1, 1)
			done := make(chan error, 1)
			go func() {
				done <- s.ReloadPlayerWithOptions(context.Background(), "self", map[string]any{"includes": []any{"matchHistory"}})
			}()
			select {
			case <-started:
			case <-time.After(time.Second):
				t.Fatal("old game completion did not start")
			}
			if action == "disconnect" {
				backend.lc.Disconnect()
			} else {
				backend.lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL, Region: "TENCENT", PlatformID: "NJ100"})
			}
			close(release)
			select {
			case err := <-done:
				if err == nil {
					t.Fatal("cancelled generation completed successfully")
				}
			case <-time.After(time.Second):
				t.Fatal("old completion did not cancel")
			}
			if len(client.Map(s.GetAll()["matchHistory"])) != 0 {
				t.Fatal("old client history polluted current state")
			}
			if action == "disconnect" {
				backend.lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL, Region: "TENCENT", PlatformID: "NJ100"})
			}
			if err := s.ReloadPlayerWithOptions(context.Background(), "self", map[string]any{"includes": []any{"matchHistory"}}); err != nil {
				t.Fatal(err)
			}
			history := client.Map(decodedRaw(client.Map(s.GetAll()["matchHistory"])["self"]))
			row := client.Map(client.List(history["data"])[0])
			if client.Map(row["data"])["owner"] != "new" {
				t.Fatal("new client could not query its own complete summary")
			}
		})
	}
}
