package client

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

func TestSGPNotificationConnectivityCountersMatchResponseInterceptor(t *testing.T) {
	events := []map[string]any{}
	c := NewWithOptions(func(ns, event string, args ...any) {
		if event == "update-state-prop/sgp-main:state" {
			events = append(events, map[string]any{"namespace": ns, "key": args[0], "value": args[1], "meta": args[2]})
		}
	}, Options{Servers: map[string]Server{"TENCENT_HN1": {Common: "https://sgp-fixture", MatchHistory: "https://sgp-fixture"}}, SGPHTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path == "/network" {
			return nil, errors.New("fixture network unavailable")
		}
		status := 200
		if r.URL.Path == "/not-found" {
			status = 404
		}
		return &http.Response{StatusCode: status, Body: io.NopCloser(strings.NewReader(`{}`)), Header: http.Header{}, Request: r}, nil
	})}})
	c.SetAuth(connectionAuth(111))
	c.mu.Lock()
	c.entitlements = "fixture"
	c.leagueSession = "fixture"
	c.tokenTime = time.Now()
	c.mu.Unlock()
	for _, path := range []string{"/ok", "/not-found", "/network", "/network", "/network", "/ok"} {
		_, _ = c.SGPJSON(context.Background(), "", "entitlements", "GET", path, nil)
	}
	state := c.SGPState()
	if state["connectionSuccessesCounted"] != 2 || state["connectionFailuresCounted"] != 3 {
		t.Fatalf("wrong connectivity counts %v", state)
	}
	if len(events) != 5 || events[3]["namespace"] != "mobx-utils-main" || events[3]["key"] != "connectionFailuresCounted" || events[3]["value"] != 3 || Map(events[3]["meta"])["raw"] != true {
		t.Fatalf("notification event shape lost %v", events)
	}
	_, _ = c.SGPJSON(context.Background(), "unknown", "entitlements", "GET", "/network", nil)
	if len(events) != 5 {
		t.Fatal("local configuration validation counted as network request")
	}
	c.Disconnect()
	state = c.SGPState()
	if state["connectionSuccessesCounted"] != 0 || state["connectionFailuresCounted"] != 0 || len(events) != 7 {
		t.Fatal("SGP server removal did not reset counters and events")
	}
}

func TestSGPLateNetworkFailureCannotRepopulateDisconnectedCounters(t *testing.T) {
	started, release := make(chan struct{}), make(chan struct{})
	c := NewWithOptions(nil, Options{Servers: map[string]Server{"TENCENT_HN1": {Common: "https://sgp-fixture", MatchHistory: "https://sgp-fixture"}}, SGPHTTPClient: &http.Client{Transport: connectionTransport(func(*http.Request) (*http.Response, error) {
		close(started)
		<-release
		return nil, errors.New("late failure")
	})}})
	c.SetAuth(connectionAuth(111))
	c.mu.Lock()
	c.entitlements = "fixture"
	c.leagueSession = "fixture"
	c.tokenTime = time.Now()
	c.mu.Unlock()
	done := make(chan error, 1)
	go func() {
		_, err := c.SGPJSON(context.Background(), "", "entitlements", "GET", "/history", nil)
		done <- err
	}()
	<-started
	c.Disconnect()
	close(release)
	waitConnectionDone(t, done)
	if c.SGPState()["connectionFailuresCounted"] != 0 {
		t.Fatal("old SGP failure recreated warning after disconnect")
	}
}

type scopeAwareSGPBody struct {
	ctx  context.Context
	data io.Reader
}

func (b *scopeAwareSGPBody) Read(p []byte) (int, error) {
	if b.ctx.Err() != nil {
		return 0, b.ctx.Err()
	}
	return b.data.Read(p)
}
func (b *scopeAwareSGPBody) Close() error { return nil }
func TestSGPResponseScopeLivesUntilBodyIsRead(t *testing.T) {
	c := NewWithOptions(nil, Options{Servers: map[string]Server{"TENCENT_HN1": {Common: "https://sgp-fixture", MatchHistory: "https://sgp-fixture"}}, SGPHTTPClient: &http.Client{Transport: connectionTransport(func(r *http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: 200, Body: &scopeAwareSGPBody{r.Context(), strings.NewReader(`{"game":123}`)}, Header: http.Header{}, Request: r}, nil
	})}})
	c.SetAuth(connectionAuth(111))
	c.mu.Lock()
	c.entitlements = "fixture"
	c.leagueSession = "fixture"
	c.tokenTime = time.Now()
	c.mu.Unlock()
	value, err := c.SGPJSON(context.Background(), "", "entitlements", "GET", "/history", nil)
	if err != nil || Number(Map(value)["game"]) != 123 {
		t.Fatalf("SGP response cancelled before caller read body %v %v", value, err)
	}
}

func TestStreamerGamePreferencesUseScopedExistingEndpointSubscription(t *testing.T) {
	const uri = "/lol-settings/v2/account/GamePreferences/game-settings"
	calls := [][]any{}
	c := New(func(ns, event string, args ...any) {
		if ns == "league-client-main" && event == "extra-lcu-event" {
			calls = append(calls, args)
		}
	})
	c.SetAuth(connectionAuth(111))
	ctx, cancel := c.scopeConnection(context.Background())
	defer cancel()
	id := c.Subscribe(uri)
	c.dispatchEventForConnection(ctx, map[string]any{"uri": uri, "eventType": "Update", "data": map[string]any{"HidePlayerNames": true}})
	if len(calls) != 1 || calls[0][0] != id || Map(Map(calls[0][1])["data"])["HidePlayerNames"] != true {
		t.Fatal("streamer preferences subscription lost event payload")
	}
	c.dispatchEventForConnection(ctx, map[string]any{"uri": "/lol-settings/v2/account/unrelated", "eventType": "Update", "data": true})
	if len(calls) != 1 {
		t.Fatal("streamer subscription exposed unrelated events")
	}
	c.Unsubscribe(id)
	c.dispatchEventForConnection(ctx, map[string]any{"uri": uri, "eventType": "Update", "data": true})
	if len(calls) != 1 {
		t.Fatal("disposed streamer subscription still receiving events")
	}
}
