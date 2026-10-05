package client

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
	"time"
)

func TestStripUnusedMissionsPreservesEveryOtherSummaryField(t *testing.T) {
	input := json.RawMessage(`{"gameId":9007199254740991,"gameMode":"KIWI","missions":{"keep":"game"},"participants":[{"puuid":"self","missions":{"unused":"progression"},"challenges":{"kda":5.5,"missions":{"keep":true}},"stats":{"kills":10,"missions":{"keep":"stats"}},"augments":[123,456],"item0":1001,"win":true},null,{"puuid":"other","kills":0}],"teams":[{"teamId":100,"missions":{"keep":"team"}}],"unknown":[{"large":9007199254740991}]}`)
	output, id, err := StripUnusedMissions(input)
	if err != nil {
		t.Fatal(err)
	}
	if id != 9007199254740991 {
		t.Fatalf("game ID changed: %d", id)
	}
	assertOnlyParticipantMissionsRemoved(t, input, output)
}

func TestStripUnusedMissionsSupportsSGPEnvelopesAndSingleWrapper(t *testing.T) {
	for _, input := range []json.RawMessage{
		json.RawMessage(`{"startIndex":0,"count":2,"metadata":{"missions":"keep"},"games":[{"metadata":{"matchId":"NJ100_99","missions":"keep"},"json":{"gameId":99,"participants":[{"missions":{"large":"unused"},"challenges":{"kda":3.2},"kills":4}]}},{"metadata":{"matchId":"NJ100_100"},"json":{"gameId":100,"participants":[{"missions":null,"deaths":5}]}},{"json":null}]}`),
		json.RawMessage(`{"metadata":{"matchId":"NJ100_99"},"json":{"gameId":99,"participants":[{"missions":{"large":"unused"},"challenges":{"teamDamagePercentage":0.2}}]}}`),
	} {
		output, _, err := StripUnusedMissions(input)
		if err != nil {
			t.Fatal(err)
		}
		assertOnlyParticipantMissionsRemoved(t, input, output)
	}
}

func TestStripUnusedMissionsReturnsUnchangedJSONWhenNotNeeded(t *testing.T) {
	for _, input := range []json.RawMessage{
		json.RawMessage(` { "gameId" : 99, "participants" : [{"challenges":{"kda":2}}] } `),
		json.RawMessage(`{"games":[],"metadata":{"missions":"keep"}}`),
		json.RawMessage(`null`),
		json.RawMessage(`{"participants":"unknown-shape","json":null}`),
	} {
		output, _, err := StripUnusedMissions(input)
		if err != nil || string(output) != string(input) {
			t.Fatalf("unneeded transformation changed original JSON: %q / %v", output, err)
		}
	}
	if _, _, err := StripUnusedMissions(json.RawMessage(`{"participants":`)); err == nil {
		t.Fatal("invalid JSON was accepted")
	}
}

func assertOnlyParticipantMissionsRemoved(t *testing.T, input, output json.RawMessage) {
	t.Helper()
	var expected, actual any
	if err := json.Unmarshal(input, &expected); err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(output, &actual); err != nil {
		t.Fatal(err)
	}
	var remove func(any)
	remove = func(value any) {
		fields, ok := value.(map[string]any)
		if !ok {
			return
		}
		if participants, ok := fields["participants"].([]any); ok {
			for _, participant := range participants {
				if fields, ok := participant.(map[string]any); ok {
					delete(fields, "missions")
				}
			}
		}
		remove(fields["json"])
		if games, ok := fields["games"].([]any); ok {
			for _, game := range games {
				remove(game)
			}
		}
	}
	remove(expected)
	if !reflect.DeepEqual(expected, actual) {
		data, _ := json.Marshal(expected)
		t.Fatalf("other summary fields changed:\nexpected %s\nactual %s", data, output)
	}
}

func TestSGPProxyStripsOnlySuccessfulGETSummary(t *testing.T) {
	input := json.RawMessage(`{"games":[{"metadata":{"matchId":"NJ100_99"},"json":{"gameId":99,"participants":[{"missions":{"unused":"large"},"challenges":{"kda":5.5},"kills":10}]}}],"count":1}`)
	for _, item := range []struct {
		name, method, path string
		status             int
		strip              bool
	}{
		{"summary", http.MethodGet, "/match-history-query/v1/products/lol/player/self/SUMMARY?count=20", 200, true},
		{"single-game", http.MethodGet, "/match-history-query/v1/products/lol/NJ100/99/SUMMARY", 200, true},
		{"trailing-slash", http.MethodGet, "/match-history-query/v1/products/lol/player/self/SUMMARY/", 200, true},
		{"post", http.MethodPost, "/match-history-query/v1/products/lol/player/self/SUMMARY", 200, false},
		{"details", http.MethodGet, "/match-history-query/v1/products/lol/NJ100/99/DETAILS", 200, false},
		{"summary-subpath", http.MethodGet, "/match-history-query/v1/products/lol/player/self/SUMMARY/extra", 200, false},
		{"error", http.MethodGet, "/match-history-query/v1/products/lol/player/self/SUMMARY", 400, false},
	} {
		t.Run(item.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(item.status)
				_, _ = w.Write(input)
			}))
			defer server.Close()
			c := NewWithOptions(nil, Options{SGPHTTPClient: server.Client(), Servers: map[string]Server{"TENCENT_NJ100": {MatchHistory: server.URL, Common: server.URL}}})
			c.SetAuth(&Auth{Region: "TENCENT", PlatformID: "NJ100"})
			c.entitlements, c.leagueSession, c.tokenTime = "fake-entitlements", "fake-session", time.Now()
			r := httptest.NewRequest(item.method, item.path, nil)
			r.Header.Set("x-akari-token-type", "entitlements")
			w := httptest.NewRecorder()
			c.SGPProxy(w, r)
			if w.Code != item.status || w.Header().Get("Content-Type") != "application/json" {
				t.Fatal("proxy changed response status or content type")
			}
			if item.strip {
				assertOnlyParticipantMissionsRemoved(t, input, w.Body.Bytes())
			} else if string(input) != w.Body.String() {
				t.Fatal("non-target response was modified")
			}
		})
	}
}

func TestSGPProxyKeepsUnexpectedSummaryPayload(t *testing.T) {
	input := `not-a-json-response`
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = io.WriteString(w, input) }))
	defer server.Close()
	c := NewWithOptions(nil, Options{SGPHTTPClient: server.Client(), Servers: map[string]Server{"TENCENT_NJ100": {MatchHistory: server.URL}}})
	c.SetAuth(&Auth{Region: "TENCENT", PlatformID: "NJ100"})
	c.entitlements, c.leagueSession, c.tokenTime = "fake-entitlements", "fake-session", time.Now()
	r := httptest.NewRequest(http.MethodGet, "/match-history-query/v1/products/lol/player/self/SUMMARY", nil)
	r.Header.Set("x-akari-token-type", "entitlements")
	w := httptest.NewRecorder()
	c.SGPProxy(w, r)
	if w.Code != 200 || w.Body.String() != input {
		t.Fatal("unexpected summary response was not passed through")
	}
}
