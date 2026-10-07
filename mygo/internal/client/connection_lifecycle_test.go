package client

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"
)

type connectionTransport func(*http.Request) (*http.Response, error)

func (f connectionTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func connectionResponse(r *http.Request, owner string) *http.Response {
	status, body := 404, `{}`
	switch r.URL.Path {
	case "/lol-summoner/v1/current-summoner":
		status, body = 200, fmt.Sprintf(`{"puuid":%q}`, owner)
	case "/lol-login/v1/login-queue-state":
		status, body = 200, `null`
	case "/lol-gameflow/v1/gameflow-phase":
		status, body = 200, `"ChampSelect"`
	case "/lol-champ-select/v1/session":
		status, body = 200, fmt.Sprintf(`{"gameId":1,"owner":%q,"localPlayerCellId":1,"myTeam":[{"cellId":1,"championId":23}]}`, owner)
	case "/lol-chat/v1/conversations":
		status, body = 200, fmt.Sprintf(`[{"id":"lol-champ-select-%s","type":"championSelect"}]`, owner)
	case "/lol-game-data/assets/v1/champion-summary.json":
		status, body = 200, fmt.Sprintf(`[{"id":23,"name":%q}]`, owner)
	case "/entitlements/v1/token":
		status, body = 200, fmt.Sprintf(`{"accessToken":%q}`, owner)
	case "/lol-league-session/v1/league-session-token":
		status, body = 200, fmt.Sprintf(`%q`, owner)
	}
	return &http.Response{StatusCode: status, Body: io.NopCloser(strings.NewReader(body)), Header: http.Header{}, Request: r}
}
func connectionAuth(pid int) *Auth {
	return &Auth{PID: pid, Port: pid, BaseURL: fmt.Sprintf("http://client-%d", pid), Password: "fixture", Region: "TENCENT", PlatformID: "HN1"}
}
func waitConnectionDone(t *testing.T, done <-chan error) error {
	t.Helper()
	select {
	case err := <-done:
		return err
	case <-time.After(2 * time.Second):
		t.Fatal("connection did not finish")
		return nil
	}
}

func TestDisconnectCancelsConnectingPollAndRejectsLateSuccess(t *testing.T) {
	started, release := make(chan *http.Request, 1), make(chan struct{})
	c := NewWithOptions(nil, Options{HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path == "/lol-summoner/v1/current-summoner" {
			started <- r
			<-release
		}
		return connectionResponse(r, "old"), nil // Intentionally ignore cancellation, reproducing late completion.
	})}})
	done := make(chan error, 1)
	go func() { done <- c.Connect(context.Background(), connectionAuth(111)) }()
	r := <-started
	if state := Map(c.State()["state"]); state["connectionState"] != "connecting" || Number(Map(state["connectingClient"])["pid"]) != 111 {
		t.Fatalf("connecting state absent %+v", state)
	}
	c.Disconnect()
	select {
	case <-r.Context().Done():
	case <-time.After(time.Second):
		t.Fatal("disconnect did not cancel pending HTTP context")
	}
	close(release)
	if waitConnectionDone(t, done) == nil {
		t.Fatal("cancelled connection succeeded")
	}
	s := c.State()
	if Map(s["state"])["connectionState"] != "disconnected" || Map(s["state"])["connectingClient"] != nil || Map(s["summoner"])["me"] != nil || c.CurrentServer() != "" || c.TokenReady() {
		t.Fatalf("late connection restored old client %+v", s)
	}
}

func TestCallerCancelledConnectClearsItsOwnPendingConnection(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	started, release := make(chan struct{}), make(chan struct{})
	c := NewWithOptions(nil, Options{HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path == "/lol-summoner/v1/current-summoner" {
			close(started)
			<-release
		}
		return connectionResponse(r, "cancelled"), nil
	})}})
	done := make(chan error, 1)
	go func() { done <- c.Connect(ctx, connectionAuth(111)) }()
	<-started
	cancel()
	close(release)
	if !errors.Is(waitConnectionDone(t, done), context.Canceled) {
		t.Fatal("caller cancellation was not propagated")
	}
	if state := Map(c.State()["state"]); state["connectionState"] != "disconnected" || state["connectingClient"] != nil || c.CurrentServer() != "" {
		t.Fatalf("cancelled own connection retained %+v", state)
	}
}

func TestDisconnectOrdersRendererEventsAfterInFlightConnectedEmission(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	var mu sync.Mutex
	states := []string{}
	c := New(func(ns, event string, args ...any) {
		if event == "update-state-prop/league-client-main:state" && args[0] == "connectionState" {
			if args[1] == "connected" {
				close(started)
				<-release
			}
			mu.Lock()
			states = append(states, String(args[1]))
			mu.Unlock()
		}
	})
	c.SetAuth(connectionAuth(111))
	ctx, cancel := c.scopeConnection(context.Background())
	defer cancel()
	setter := make(chan struct{})
	go func() { c.setForConnection(ctx, "state", "connectionState", "connected"); close(setter) }()
	<-started
	disconnected := make(chan struct{})
	go func() { c.Disconnect(); close(disconnected) }()
	close(release)
	<-setter
	<-disconnected
	mu.Lock()
	defer mu.Unlock()
	if len(states) != 2 || states[0] != "connected" || states[1] != "disconnected" {
		t.Fatalf("renderer received an old connected state after disconnect: %v", states)
	}
}

func TestSwitchPIDRejectsOldPollSuccessFailureAndDependentWrites(t *testing.T) {
	for _, blocked := range []string{"initial", "failure", "champ-select", "chat", "assets", "tokens"} {
		t.Run(blocked, func(t *testing.T) {
			paths := map[string]string{"initial": "/lol-summoner/v1/current-summoner", "failure": "/lol-summoner/v1/current-summoner", "champ-select": "/lol-champ-select/v1/session", "chat": "/lol-chat/v1/conversations", "assets": "/lol-game-data/assets/v1/champion-summary.json", "tokens": "/entitlements/v1/token"}
			started, release := make(chan *http.Request, 1), make(chan struct{})
			var once sync.Once
			c := NewWithOptions(nil, Options{HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
				owner := "new"
				if r.URL.Host == "client-111" {
					owner = "old"
					if r.URL.Path == paths[blocked] {
						once.Do(func() { started <- r; <-release })
						if blocked == "failure" {
							return nil, errors.New("old transport failed")
						}
					}
				}
				return connectionResponse(r, owner), nil
			})}})
			oldDone := make(chan error, 1)
			go func() { oldDone <- c.Connect(context.Background(), connectionAuth(111)) }()
			r := <-started
			newDone := make(chan error, 1)
			go func() { newDone <- c.Connect(context.Background(), connectionAuth(222)) }()
			select {
			case <-r.Context().Done():
			case <-time.After(time.Second):
				t.Fatal("switch did not cancel old HTTP context")
			}
			if blocked != "tokens" {
				if err := waitConnectionDone(t, newDone); err != nil {
					t.Fatal(err)
				}
			}
			close(release)
			if waitConnectionDone(t, oldDone) == nil {
				t.Fatal("superseded old connection reported success")
			}
			if blocked == "tokens" {
				if err := waitConnectionDone(t, newDone); err != nil {
					t.Fatal(err)
				}
			}
			s := c.State()
			if state := Map(s["state"]); state["connectionState"] != "connected" || Number(Map(state["auth"])["pid"]) != 222 || state["connectingClient"] != nil {
				t.Fatalf("old poll replaced new connection %+v", state)
			}
			if Map(Map(s["summoner"])["me"])["puuid"] != "new" || Map(Map(s["champSelect"])["session"])["owner"] != "new" || Map(Map(Map(s["gameData"])["champions"])["23"])["name"] != "new" {
				t.Fatalf("old data replaced new selected PID %+v", s)
			}
			if String(Map(Map(Map(s["chat"])["conversations"])["championSelect"])["id"]) != "lol-champ-select-new" {
				t.Fatal("old chat replaced new connection")
			}
			c.mu.RLock()
			tokensCorrect := c.entitlements == "new" && c.leagueSession == "new"
			c.mu.RUnlock()
			if !tokensCorrect {
				t.Fatal("old token contaminated new credentials")
			}
		})
	}
}

func TestDisconnectDuringDiscoveryCannotConnectLateDiscoveredClient(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	writes := 0
	c := NewWithOptions(nil, Options{Discover: func(context.Context) (*Auth, error) { close(started); <-release; return connectionAuth(111), nil }, HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) { writes++; return connectionResponse(r, "late"), nil })}})
	done := make(chan error, 1)
	go func() { done <- c.PollOnce(context.Background()) }()
	<-started
	c.Disconnect()
	close(release)
	if waitConnectionDone(t, done) == nil || writes != 0 || c.CurrentServer() != "" {
		t.Fatal("late discovery connected after manual disconnect")
	}
}

func TestAutoConnectToggleRestoresManualDisconnectAndMultipleClientsWait(t *testing.T) {
	for _, count := range []int{1, 2} {
		t.Run(fmt.Sprint(count), func(t *testing.T) {
			calls := 0
			c := NewWithOptions(nil, Options{Discover: func(context.Context) (*Auth, error) {
				calls++
				candidates := []*Auth{connectionAuth(111)}
				if count == 2 {
					candidates = append(candidates, connectionAuth(222))
				}
				return uniqueDiscoveredConnection(candidates), nil
			}, HTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) { return connectionResponse(r, "discovered"), nil })}})
			c.Disconnect()
			if err := c.PollOnce(context.Background()); err != nil || calls != 0 {
				t.Fatal("manual disconnect auto-reconnected")
			}
			c.SetAutoConnect(true)
			_ = c.PollOnce(context.Background())
			if calls != 0 {
				t.Fatal("unchanged auto-connect flag cleared manual disconnect")
			}
			c.SetAutoConnect(false)
			c.SetAutoConnect(true)
			if err := c.PollOnce(context.Background()); err != nil {
				t.Fatal(err)
			}
			connected := Map(c.State()["state"])["connectionState"] == "connected"
			if calls != 1 || connected != (count == 1) {
				t.Fatalf("wrong auto connection selection calls=%d connected=%t", calls, connected)
			}
		})
	}
}

func TestDiscoveryDeduplicatesSharedLCUEndpointButLeavesDistinctClients(t *testing.T) {
	a := connectionAuth(111)
	b := *a
	b.PID = 222
	b.Region = ""
	b.PlatformID = ""
	if result := uniqueDiscoveredConnection([]*Auth{&b, a}); result == nil || result.Region != "TENCENT" || result.PlatformID != "HN1" {
		t.Fatal("same LCU backend/UX duplicates prevented auto connect")
	}
	if uniqueDiscoveredConnection([]*Auth{a, connectionAuth(222)}) != nil {
		t.Fatal("multiple clients incorrectly auto-selected first process")
	}
}
