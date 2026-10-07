package misc

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type controlledLoginTimer struct {
	delay    time.Duration
	callback func()
	stopped  bool
}
type profileEventClient struct {
	httpJSON
	mu    sync.Mutex
	state map[string]any
}

func (c *profileEventClient) State() map[string]any {
	c.mu.Lock()
	defer c.mu.Unlock()
	return client.Clone(c.state).(map[string]any)
}
func (c *profileEventClient) presence(value any) {
	c.mu.Lock()
	defer c.mu.Unlock()
	client.Map(c.state["chat"])["me"] = value
}
func (c *profileEventClient) connection(state string, pid int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.state["state"] = map[string]any{"connectionState": state, "auth": map[string]any{"pid": pid}}
}

func startTimerFixture(t *testing.T, lc *client.Client) (*Service, *profileEventClient, chan *controlledLoginTimer, context.CancelFunc, chan struct{}) {
	t.Helper()
	store := newStore(t)
	set(t, store, "autoSetStatusMessageEnabled", true)
	set(t, store, "statusMessage", "automatic")
	set(t, store, "autoSetRankedStatusEnabled", true)
	c := &profileEventClient{httpJSON: httpJSON{lc}, state: map[string]any{"state": map[string]any{"connectionState": "connected", "auth": map[string]any{"pid": 1}}, "chat": map[string]any{"me": map[string]any{"availability": "chat", "summonerId": 10, "statusMessage": "initial"}}}}
	s := New(c, store, nil)
	timers := make(chan *controlledLoginTimer, 8)
	s.after = func(delay time.Duration, callback func()) func() {
		timer := &controlledLoginTimer{delay: delay, callback: callback}
		timers <- timer
		return func() { timer.stopped = true }
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { defer close(done); s.Run(ctx) }()
	t.Cleanup(func() { cancel(); <-done; s.Close() })
	return s, c, timers, cancel, done
}

func nextTimer(t *testing.T, timers <-chan *controlledLoginTimer) *controlledLoginTimer {
	t.Helper()
	select {
	case timer := <-timers:
		if timer.delay != 2*time.Second {
			t.Fatal("original settle delay changed", timer.delay)
		}
		return timer
	case <-time.After(time.Second):
		t.Fatal("chat profile event did not schedule timer")
		return nil
	}
}

func TestChatProfileEventsApplyAfterTwoSecondsWithoutTickOrHTTPPolling(t *testing.T) {
	var mu sync.Mutex
	requests := []map[string]any{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Error("event-driven profile automation polled HTTP", r.Method, r.URL.Path)
		}
		var body map[string]any
		_ = json.NewDecoder(r.Body).Decode(&body)
		mu.Lock()
		requests = append(requests, body)
		mu.Unlock()
		_, _ = fmt.Fprint(w, `{}`)
	}))
	defer server.Close()
	lc := client.NewWithOptions(nil, client.Options{HTTPClient: server.Client()})
	lc.SetAuth(&client.Auth{PID: 1, BaseURL: server.URL})
	s, c, timers, _, _ := startTimerFixture(t, lc)
	initial := nextTimer(t, timers)
	c.presence(map[string]any{"availability": "chat", "summonerId": 10, "statusMessage": "last change", "lol": map[string]any{"rankedLeagueTier": "GOLD"}})
	if err := s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:chat", "me"); err != nil {
		t.Fatal(err)
	}
	settled := nextTimer(t, timers)
	if !initial.stopped {
		t.Fatal("profile change did not restart settle timer")
	}
	initial.callback()
	mu.Lock()
	if len(requests) != 0 {
		t.Fatal("stale profile timer wrote early")
	}
	mu.Unlock()
	settled.callback()
	mu.Lock()
	if len(requests) != 2 {
		t.Fatal("two-second timer did not apply both switches", requests)
	}
	mu.Unlock()
	c.presence(map[string]any{"availability": "away", "summonerId": 10})
	_ = s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:chat", "me")
	select {
	case <-timers:
		t.Fatal("login automation repeated within same connection")
	default:
	}
	c.presence(nil)
	_ = s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:chat", "me")
	c.presence(map[string]any{"availability": "chat", "summonerId": 10})
	_ = s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:chat", "me")
	pending := nextTimer(t, timers)
	if _, err := s.Call(context.Background(), "applyStatusMessage", []any{"manual"}); err != nil {
		t.Fatal(err)
	}
	if !pending.stopped {
		t.Fatal("manual apply did not stop settle task")
	}
	pending.callback()
	mu.Lock()
	defer mu.Unlock()
	if len(requests) != 3 || requests[2]["statusMessage"] != "manual" {
		t.Fatal("late timer overwrote manual apply", requests)
	}
}

func TestLoginTimerKeepsSchedulingConnectionScopeAcrossDisconnectAndPIDSwitch(t *testing.T) {
	for _, action := range []string{"disconnect", "switch"} {
		t.Run(action, func(t *testing.T) {
			oldWrites, newWrites := 0, 0
			old := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { oldWrites++; _, _ = fmt.Fprint(w, `{}`) }))
			defer old.Close()
			fresh := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { newWrites++; _, _ = fmt.Fprint(w, `{}`) }))
			defer fresh.Close()
			lc := client.NewWithOptions(nil, client.Options{HTTPClient: old.Client()})
			lc.SetAuth(&client.Auth{PID: 1, BaseURL: old.URL})
			s, c, timers, _, _ := startTimerFixture(t, lc)
			stale := nextTimer(t, timers)
			if action == "disconnect" {
				lc.Disconnect()
				c.connection("disconnected", 1)
			} else {
				lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL})
				c.connection("connected", 2)
			}
			// Even before the state notification reaches this service, the original
			// connection-bound context must prevent a callback writing to the new PID.
			stale.callback()
			if oldWrites != 0 || newWrites != 0 {
				t.Fatal("stale timer crossed connection generation")
			}
			_ = s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:state", "connectionState")
			if action == "disconnect" {
				lc.SetAuth(&client.Auth{PID: 2, BaseURL: fresh.URL})
				c.connection("connected", 2)
				_ = s.HandleEvent(context.Background(), "mobx-utils-main", "update-state-prop/league-client-main:state", "auth")
			}
			current := nextTimer(t, timers)
			current.callback()
			if oldWrites != 0 || newWrites != 2 {
				t.Fatalf("new login automations not scoped to new client: old=%d new=%d", oldWrites, newWrites)
			}
		})
	}
}
