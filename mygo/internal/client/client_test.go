package client

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestCredentialsAndLockfile(t *testing.T) {
	for _, platform := range []string{"rso_platform_id", "rso-platform-id"} {
		auth := ParseCommandLine(fmt.Sprintf(`"C:\WeGameApps\英雄联盟\LeagueClientUx.exe" --app-port="57830" --app-pid="1234" --remoting-auth-token="test-only" --region="TENCENT" --%s="NJ100"`, platform), 4567)
		if auth == nil || auth.Port != 57830 || auth.PID != 1234 || auth.PlatformID != "NJ100" {
			t.Fatal("quoted WeGame credentials were not parsed")
		}
		public, _ := json.Marshal(auth.Public())
		if strings.Contains(string(public), "test-only") {
			t.Fatal("public state contains credentials")
		}
	}
	if ParseCommandLine(`--app-port=70000 --remoting-auth-token=test`, 1234) != nil {
		t.Fatal("invalid port was accepted")
	}
	if ParseLockfile("") != nil || ParseLockfile("LeagueClient:1234:57830:test:http") != nil {
		t.Fatal("invalid lockfile was accepted")
	}
	if auth := ParseLockfile("LeagueClient:1234:57830:test:https"); auth == nil || auth.Port != 57830 {
		t.Fatal("lockfile was not parsed")
	}
}

func TestLCUPollAndImageProxy(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		user, password, ok := r.BasicAuth()
		if !ok || user != "riot" || password != "test-only" {
			t.Error("LCU basic authentication missing")
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/lol-summoner/v1/current-summoner":
			io.WriteString(w, `{"puuid":"self","gameName":"玩家","tagLine":"1000"}`)
		case "/lol-gameflow/v1/gameflow-phase":
			io.WriteString(w, `"ChampSelect"`)
		case "/lol-champ-select/v1/session":
			io.WriteString(w, `{"localPlayerCellId":0,"myTeam":[{"cellId":0,"championId":103}],"benchEnabled":true}`)
		case "/entitlements/v1/token":
			io.WriteString(w, `{"accessToken":"ent-test"}`)
		case "/lol-league-session/v1/league-session-token":
			io.WriteString(w, `"league-test"`)
		case "/lol-game-data/assets/v1/champion-summary.json":
			io.WriteString(w, `[{"id":103,"name":"阿狸"}]`)
		case "/lol-game-data/assets/test.png":
			w.Header().Set("Content-Type", "image/png")
			w.Write([]byte{1, 2, 3})
		default:
			io.WriteString(w, `[]`)
		}
	}))
	defer server.Close()
	var eventsMu sync.Mutex
	eventCount := 0
	c := NewWithOptions(func(namespace, name string, args ...any) {
		eventsMu.Lock()
		eventCount++
		eventsMu.Unlock()
		if namespace != "mobx-utils-main" {
			t.Error("wrong state event namespace")
		}
	}, Options{HTTPClient: server.Client(), Discover: func(context.Context) (*Auth, error) {
		return &Auth{Port: 1234, Password: "test-only", Region: "TENCENT", PlatformID: "NJ100", BaseURL: server.URL}, nil
	}})
	if err := c.PollOnce(context.Background()); err != nil {
		t.Fatal(err)
	}
	state := c.State()
	if Map(state["state"])["connectionState"] != "connected" || Number(Map(state["champSelect"])["currentChampion"]) != 103 {
		t.Fatal("poll did not synchronize active selection")
	}
	public, _ := json.Marshal(state)
	if strings.Contains(string(public), "test-only") || strings.Contains(string(public), "ent-test") {
		t.Fatal("state leaked credentials")
	}
	if c.CurrentServer() != "TENCENT_NJ100" || !c.TokenReady() {
		t.Fatal("SGP readiness did not follow Tencent client")
	}
	if eventCount == 0 {
		t.Fatal("state events were not emitted")
	}
	response := httptest.NewRecorder()
	c.Proxy(response, httptest.NewRequest(http.MethodGet, "/lol-game-data/assets/test.png", nil))
	if response.Code != 200 || response.Header().Get("Content-Type") != "image/png" || response.Body.Len() != 3 {
		t.Fatal("image proxy lost response")
	}
	blocked := httptest.NewRecorder()
	c.Proxy(blocked, httptest.NewRequest(http.MethodGet, "/entitlements/v1/token", nil))
	if blocked.Code != http.StatusForbidden {
		t.Fatal("browser can request SGP credentials")
	}
}

func TestSGPProxyUsesEndpointTokenAndContentLength(t *testing.T) {
	lcu := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/entitlements/v1/token" {
			io.WriteString(w, `{"accessToken":"ent-test"}`)
		} else {
			io.WriteString(w, `"league-test"`)
		}
	}))
	defer lcu.Close()
	sgp := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/summoner-ledge/v1/regions/NJ100/summoners/puuids" {
			t.Error("SGP region placeholder was not replaced")
		}
		if r.Header.Get("Authorization") != "Bearer league-test" || r.ContentLength <= 0 || len(r.TransferEncoding) > 0 {
			t.Error("wrong token or chunked summoner request")
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `[{"puuid":"test"}]`)
	}))
	defer sgp.Close()
	c := NewWithOptions(nil, Options{HTTPClient: lcu.Client(), SGPHTTPClient: sgp.Client(), Servers: map[string]Server{"TENCENT_NJ100": {Common: sgp.URL, MatchHistory: sgp.URL}}})
	c.SetAuth(&Auth{Password: "local-only", Region: "TENCENT", PlatformID: "NJ100", BaseURL: lcu.URL})
	r := httptest.NewRequest(http.MethodPost, "/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", strings.NewReader(`["test"]`))
	r.Header.Set("x-akari-token-type", "league-session")
	r.ContentLength = 0 // Native scheme requests carry a body without a declared length.
	w := httptest.NewRecorder()
	c.SGPProxy(w, r)
	if w.Code != 200 {
		t.Fatalf("SGP proxy failed with status %d", w.Code)
	}
}

func TestLiveReadOnly(t *testing.T) {
	if os.Getenv("TIMO_LIVE_READONLY") != "1" {
		t.Skip("explicit local-client read verification only")
	}
	auth, err := Discover(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if auth == nil {
		t.Fatal("LOL client not found")
	}
	c := New(nil)
	c.SetAuth(auth)
	me, err := c.JSON(context.Background(), http.MethodGet, "/lol-summoner/v1/current-summoner", nil)
	if err != nil {
		t.Fatal(err)
	}
	puuid := String(Map(me)["puuid"])
	if puuid == "" {
		t.Fatal("current summoner identity missing")
	}
	data, err := c.SGPJSON(context.Background(), "", "entitlements", http.MethodGet, "/match-history-query/v1/products/lol/player/"+puuid+"/SUMMARY?startIndex=0&count=10", nil)
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("read-only LCU connected; server=%s; SGP summaries=%d", c.CurrentServer(), len(List(Map(data)["games"])))
	for _, endpoint := range []string{"/summoner-ledge/v1/regions/@akari:sgpServerSubId@/summoners/puuids", "/challenges-client/v2/all-player-data/?puuid=" + puuid} {
		body := "[]"
		if strings.Contains(endpoint, "summoner-ledge") {
			encoded, _ := json.Marshal([]string{puuid})
			body = string(encoded)
		}
		r := httptest.NewRequest(http.MethodPost, endpoint, strings.NewReader(body))
		r.ContentLength = 0
		r.Header.Set("x-akari-token-type", "league-session")
		w := httptest.NewRecorder()
		c.SGPProxy(w, r)
		if w.Code >= 400 {
			t.Fatalf("read-only SGP POST failed: status=%d endpoint=%s", w.Code, strings.Split(endpoint, "?")[0])
		}
		t.Logf("read-only SGP POST succeeded: status=%d endpoint=%s", w.Code, strings.Split(endpoint, "?")[0])
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- c.consumeEvents(ctx) }()
	connected := false
	for attempt := 0; attempt < 20; attempt++ {
		c.mu.RLock()
		connected = c.eventsConnected
		c.mu.RUnlock()
		if connected {
			break
		}
		select {
		case err := <-done:
			t.Fatalf("LCU read-only event stream failed: %v", err)
		case <-time.After(20 * time.Millisecond):
		}
	}
	if !connected {
		t.Fatal("LCU read-only event stream did not connect")
	}
	cancel()
	<-done
	t.Log("read-only LCU event stream handshake and OnJsonApiEvent subscription succeeded")
}
