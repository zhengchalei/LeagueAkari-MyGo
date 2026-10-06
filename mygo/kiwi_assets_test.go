package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/catalog"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func TestBundledKiwiBalanceIsIndependentAvailableOfflineAndRecoverable(t *testing.T) {
	d := &Desktop{static: staticStates()}
	state := d.state("extra-assets-main", "kiwi")
	balance := client.Map(state["balance"])
	if len(balance) != 173 || state["version"] != "16.19" || state["cached"] != true || state["sourceUrl"] != catalog.KiwiSourceURL {
		t.Fatal("bundled source metadata/coverage missing")
	}
	adjustments := client.List(client.Map(balance["147"])["adjustments"])
	if len(adjustments) != 3 {
		t.Fatal("Seraphine's real source adjustments are missing")
	}
	for _, adjustment := range adjustments {
		if client.Map(adjustment)["effect"] != "nerfed" {
			t.Fatal("Seraphine's reductions were reported as buffs")
		}
	}
	directory := t.TempDir()
	data, err := os.ReadFile("internal/bridge/assets/kiwi-balance.json")
	if err != nil {
		t.Fatal(err)
	}
	data = []byte(strings.Replace(string(data), `"version": "16.19"`, `"version": "fixture-persisted-version"`, 1))
	if err := os.WriteFile(filepath.Join(directory, "kiwi-balance.json"), data, 0600); err != nil {
		t.Fatal(err)
	}
	d.loadKiwiBalance(directory)
	if d.state("extra-assets-main", "kiwi")["version"] != "fixture-persisted-version" {
		t.Fatal("last saved dataset wasn't loaded on restart")
	}
	var unavailable atomic.Bool
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if unavailable.Load() {
			http.Error(w, "offline", 503)
			return
		}
		switch r.URL.Path {
		case "/index.html":
			fmt.Fprint(w, `<iframe src="/content/index.html"></iframe>`)
		case "/content/api/v1/versions.js":
			fmt.Fprint(w, `export default [{"version":"16.20","collectedAt":"new"}]`)
		case "/content/api/v1/versions/16.20/champions.js":
			fmt.Fprint(w, `export default {"items":[{"id":147}]}`)
		case "/content/api/v1/versions/16.20/champions/147.js":
			fmt.Fprint(w, `export default {"champion":{"id":147},"bb":{"造成伤害":"93%"}}`)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	d.externalClient = server.Client()
	if err := d.fetchKiwiBalance(context.Background(), server.URL+"/index.html", directory); err != nil {
		t.Fatal(err)
	}
	if d.state("extra-assets-main", "kiwi")["version"] != "16.20" {
		t.Fatal("live source version did not reach renderer state")
	}
	saved, err := os.ReadFile(filepath.Join(directory, "kiwi-balance.json"))
	if err != nil {
		t.Fatal(err)
	}
	var persisted catalog.KiwiSnapshot
	if json.Unmarshal(saved, &persisted) != nil || persisted.Version != "16.20" || persisted.Balance["147"].Adjustments[0].Value != .93 {
		t.Fatal("successful source wasn't persisted")
	}
	unavailable.Store(true)
	if err := d.fetchKiwiBalance(context.Background(), server.URL+"/index.html", directory); err == nil {
		t.Fatal("unavailable source reported success")
	}
	state = d.state("extra-assets-main", "kiwi")
	if state["version"] != "16.20" || state["cached"] != true || len(client.Map(state["balance"])) != 1 {
		t.Fatal("failed refresh cleared or mixed the cached snapshot")
	}
}
