package misc

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestMiscHTTPFullPresenceSettlesAndManualApplyKeepsRankContract(t *testing.T) {
	var mu sync.Mutex
	me := map[string]any{"availability": "chat", "summonerId": 10, "statusMessage": "initial", "lol": map[string]any{"rankedLeagueTier": "GOLD"}}
	writes := []map[string]any{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		if r.Method == "GET" {
			_ = json.NewEncoder(w).Encode(me)
			return
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		writes = append(writes, body)
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	set(t, store, "statusMessage", "automatic")
	set(t, store, "autoSetRankedStatusEnabled", true)
	s := New(lc, store, nil)
	defer s.Close()
	// Only JSON is needed here; connection state is controlled by the real
	// backend in production, while this fixture drives the presence protocol.
	s.client = httpJSON{lc}
	now := time.Unix(100, 0)
	s.now = func() time.Time { return now }
	_ = s.Tick(context.Background())
	now = now.Add(time.Second)
	mu.Lock()
	me["statusMessage"] = "changed"
	mu.Unlock()
	_ = s.Tick(context.Background())
	now = now.Add(time.Second)
	_ = s.Tick(context.Background())
	mu.Lock()
	if len(writes) != 0 {
		t.Fatal("partial me signature applied before full presence settled", writes)
	}
	mu.Unlock()
	now = now.Add(time.Second)
	if err := s.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Call(context.Background(), "applyRankedStatus", []any{map[string]any{"queue": "RANKED_FLEX_SR", "tier": "GOLD", "division": "II"}}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Call(context.Background(), "applyStatusMessage", []any{"manual"}); err != nil {
		t.Fatal(err)
	}
	now = now.Add(time.Hour)
	_ = s.Tick(context.Background())
	mu.Lock()
	defer mu.Unlock()
	if len(writes) != 4 || writes[0]["statusMessage"] != "automatic" || writes[3]["statusMessage"] != "manual" {
		t.Fatal("automatic once/manual status contract drift", writes)
	}
	if _, ok := writes[1]["lol"].(map[string]any)["rankedLeagueDivision"]; ok {
		t.Fatal("apex division must be omitted")
	}
	if writes[2]["lol"].(map[string]any)["rankedLeagueDivision"] != "II" {
		t.Fatal("ordinary rank division lost")
	}
}

func TestManualHTTPStatusInterruptsRunningLoginAutomation(t *testing.T) {
	started, cancelled := make(chan struct{}, 1), make(chan struct{}, 1)
	var mu sync.Mutex
	writes := []string{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == "GET" {
			_, _ = fmt.Fprint(w, `{"availability":"chat","summonerId":10}`)
			return
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		if body["statusMessage"] == "automatic" {
			started <- struct{}{}
			<-r.Context().Done()
			cancelled <- struct{}{}
			return
		}
		mu.Lock()
		writes = append(writes, body["statusMessage"].(string))
		mu.Unlock()
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	set(t, store, "statusMessage", "automatic")
	s := New(httpJSON{lc}, store, nil)
	defer s.Close()
	now := time.Unix(100, 0)
	s.now = func() time.Time { return now }
	_ = s.Tick(context.Background())
	now = now.Add(2 * time.Second)
	done := make(chan error, 1)
	go func() { done <- s.Tick(context.Background()) }()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("automatic write not in flight")
	}
	if _, err := s.Call(context.Background(), "applyStatusMessage", []any{"manual"}); err != nil {
		t.Fatal(err)
	}
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal("manual status did not cancel automation", err)
	}
	select {
	case <-cancelled:
	case <-time.After(time.Second):
		t.Fatal("automatic HTTP request not cancelled")
	}
	mu.Lock()
	defer mu.Unlock()
	if len(writes) != 1 || writes[0] != "manual" {
		t.Fatal("manual preference overwritten", writes)
	}
}

type httpJSON struct{ lc *client.Client }

func (c httpJSON) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return c.lc.JSON(ctx, method, path, body)
}
func (c httpJSON) RequestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	return c.lc.RequestScope(ctx)
}

func TestMiscHTTPAwayReplyOfflineLockAndFailureEvent(t *testing.T) {
	var mu sync.Mutex
	availability := "chat"
	posts, offline := 0, 0
	fail := false
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		if r.Method == "POST" {
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			if body["body"] != "稍后回复" || body["type"] != "chat" {
				t.Error("auto reply protocol changed", body)
			}
			posts++
			if fail {
				http.Error(w, "send failed", 500)
				return
			}
			_, _ = fmt.Fprint(w, `{}`)
			return
		}
		if r.Method == "PUT" {
			var body map[string]any
			_ = json.NewDecoder(r.Body).Decode(&body)
			if body["availability"] == "offline" {
				offline++
			}
			_, _ = fmt.Fprint(w, `{}`)
			return
		}
		if r.URL.Path == "/lol-chat/v1/me" {
			_ = json.NewEncoder(w).Encode(map[string]any{"availability": availability, "summonerId": 10})
			return
		}
		if r.URL.Path == "/lol-summoner/v1/current-summoner" {
			_, _ = fmt.Fprint(w, `{"summonerId":10}`)
			return
		}
		_, _ = fmt.Fprint(w, `[]`)
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	store := newStore(t)
	set(t, store, "autoReplyEnabled", true)
	set(t, store, "autoReplyEnableOnAway", true)
	set(t, store, "autoReplyText", "稍后回复")
	set(t, store, "lockOfflineStatus", true)
	events := 0
	s := New(httpJSON{lc}, store, func(ns, name string, args ...any) {
		if ns == "auto-misc-main" && name == "error-send-failed" {
			value := args[0].(map[string]any)["error"].(map[string]any)
			if value["message"] == "" {
				t.Error("failure message lost")
			}
			events++
		}
	})
	defer s.Close()
	message := chatMessage{Type: "chat", FromSummonerID: 20}
	if err := s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/1", "Create", message); err != nil {
		t.Fatal(err)
	}
	mu.Lock()
	availability = "away"
	mu.Unlock()
	if err := s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/1", "Create", message); err != nil {
		t.Fatal(err)
	}
	_ = s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/1", "Update", message)
	message.FromSummonerID = 10
	_ = s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/2", "Create", message)
	message.FromSummonerID = 20
	mu.Lock()
	fail = true
	mu.Unlock()
	if err := s.HandleLCUEvent(context.Background(), "/lol-chat/v1/conversations/friend/messages/3", "Create", message); err == nil {
		t.Fatal("failed reply did not return HTTP error")
	}
	_ = s.HandleLCUEvent(context.Background(), "/lol-chat/v1/me", "Update", map[string]any{"availability": "offline"})
	_ = s.HandleLCUEvent(context.Background(), "/lol-chat/v1/me", "Update", map[string]any{"availability": "chat"})
	mu.Lock()
	defer mu.Unlock()
	if posts != 2 || offline != 1 || events != 1 {
		t.Fatalf("away/self/dedup/offline/error behavior changed: posts=%d offline=%d events=%d", posts, offline, events)
	}
}
