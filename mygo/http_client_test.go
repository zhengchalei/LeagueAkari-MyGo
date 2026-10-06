package main

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"strconv"
	"sync/atomic"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestExternalProxyAppliesToSGPAndAssetsAndCanChangeWithoutRestart(t *testing.T) {
	var directRequests, proxyRequests atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		directRequests.Add(1)
		fmt.Fprint(w, `{"via":"direct"}`)
	}))
	defer target.Close()
	proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		proxyRequests.Add(1)
		if !r.URL.IsAbs() {
			t.Error("external request did not use an HTTP proxy")
		}
		fmt.Fprint(w, `{"via":"proxy"}`)
	}))
	defer proxy.Close()
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	proxyURL, _ := url.Parse(proxy.URL)
	port, _ := strconv.Atoi(proxyURL.Port())
	if err := store.Set("app-common-main", "httpProxy", object{"strategy": "force", "host": proxyURL.Hostname(), "port": port}); err != nil {
		t.Fatal(err)
	}
	httpClient := newExternalHTTPClient(store)
	defer httpClient.CloseIdleConnections()
	d := &Desktop{store: store, externalClient: httpClient}
	value, err := d.fetchJSON(context.Background(), target.URL+"/assets")
	if err != nil || client.Map(value)["via"] != "proxy" {
		t.Fatalf("remote asset did not follow proxy: %v %v", value, err)
	}
	lcu := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/entitlements/v1/token":
			io.WriteString(w, `{"accessToken":"fixture-entitlements"}`)
		case "/lol-league-session/v1/league-session-token":
			io.WriteString(w, `"fixture-session"`)
		default:
			http.NotFound(w, r)
		}
	}))
	defer lcu.Close()
	c := client.NewWithOptions(nil, client.Options{SGPHTTPClient: httpClient, Servers: map[string]client.Server{
		"TENCENT_HN1": {Common: target.URL, MatchHistory: target.URL},
	}})
	c.SetAuth(&client.Auth{Region: "TENCENT", PlatformID: "HN1", BaseURL: lcu.URL, Password: "fixture-only"})
	value, err = c.SGPJSON(context.Background(), "", "league-session", http.MethodGet, "/summoner", nil)
	if err != nil || client.Map(value)["via"] != "proxy" || proxyRequests.Load() != 2 || directRequests.Load() != 0 {
		t.Fatalf("SGP or local token request used the wrong transport: %v %v", value, err)
	}
	if err := store.Set("app-common-main", "httpProxy", object{"strategy": "disable"}); err != nil {
		t.Fatal(err)
	}
	value, err = c.SGPJSON(context.Background(), "", "league-session", http.MethodGet, "/summoner", nil)
	if err != nil || client.Map(value)["via"] != "direct" || directRequests.Load() != 1 {
		t.Fatalf("existing SGP client did not apply disabled proxy: %v %v", value, err)
	}
}
