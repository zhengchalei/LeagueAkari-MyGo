package catalog

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
)

func TestKiwiAdjustmentsKeepUnitsDamageDirectionAndUnlistedHeroes(t *testing.T) {
	champion, err := ParseKiwiChampion([]byte(`export default {"champion":{"id":23},"bb":{"承受伤害":"90%","造成伤害":"110%","治疗效果":"120%","技能急速":-10,"韧性":"20","攻击速度增长":"+2.5%","施法资源回复":"120%","护盾效果":"100%","特殊规则":"技能有特殊调整"}};`), 23)
	if err != nil {
		t.Fatal(err)
	}
	fields := map[string]KiwiAdjustment{}
	for _, entry := range champion.Adjustments {
		fields[entry.Type] = entry
	}
	if fields["damage-taken"].Value != .9 || fields["damage-taken"].Effect != "buffed" || fields["damage-dealt"].Value != 1.1 || fields["damage-dealt"].Effect != "buffed" {
		t.Fatal("damage multiplier or reversed damage-taken direction changed", fields)
	}
	if fields["ability-haste"].Value != -10 || fields["ability-haste"].Display != "literal" || fields["ability-haste"].Effect != "nerfed" || fields["tenacity"].Display != "literal" {
		t.Fatal("signed numeric fields were interpreted as multipliers", fields)
	}
	if fields["attack-speed-growth"].Value != 2.5 || fields["attack-speed-growth"].FormattedValue != "+2.5%" || fields["resource-regen"].Value != 1.2 {
		t.Fatal("growth decimal or casting resource recovery lost", fields)
	}
	if _, exists := fields["shielding"]; exists || fields["special"].Effect != "neutral" || fields["special"].Description != "特殊规则 技能有特殊调整" {
		t.Fatal("neutral baseline or unknown effect was guessed", fields)
	}
	champion, err = ParseKiwiChampion([]byte(`export default {"champion":{"id":103}}`), 103)
	if err != nil || champion.Adjustments == nil || len(champion.Adjustments) != 0 {
		t.Fatal("an unlisted adjustment needs a known empty array, not missing champion data", err)
	}
	for _, payload := range []string{`export default {"champion":{"id":147}}`, `export default window.stealData()`, `export default {"champion":{"id":103}}; alert(1)`, `<!doctype html>Forbidden`} {
		if _, err := ParseKiwiChampion([]byte(payload), 103); err == nil {
			t.Fatal("wrong hero or executable module accepted:", payload)
		}
	}
}

func TestKiwiSourceFollowsToyRevisionAndRefreshesOnlyCompleteChangedDatasets(t *testing.T) {
	var newer, failed atomic.Bool
	var detailRequests atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet {
			t.Errorf("unexpected source mutation: %s", r.Method)
		}
		if r.URL.Path == "/toy/resg/index.html" {
			fmt.Fprint(w, `<iframe src="/toy/resg/revision-2/index.html"></iframe>`)
			return
		}
		base := "/toy/resg/revision-2/api/v1/"
		switch strings.TrimPrefix(r.URL.Path, base) {
		case "versions.js":
			version := "16.19"
			if newer.Load() {
				version = "16.20"
			}
			fmt.Fprintf(w, `export default [{"version":%q,"collectedAt":"fixture-date"}]`, version)
		case "versions/16.19/champions.js", "versions/16.20/champions.js":
			fmt.Fprint(w, `export default {"items":[{"id":23},{"id":147},{"id":103}]}`)
		default:
			if strings.HasSuffix(r.URL.Path, "/champions/23.js") {
				detailRequests.Add(1)
				fmt.Fprint(w, `export default {"champion":{"id":23},"bb":{"承受伤害":"90%"}}`)
			} else if strings.HasSuffix(r.URL.Path, "/champions/147.js") {
				detailRequests.Add(1)
				if failed.Load() {
					http.Error(w, "offline", 503)
					return
				}
				fmt.Fprint(w, `export default {"champion":{"id":147},"bb":{"造成伤害":"90%"}}`)
			} else if strings.HasSuffix(r.URL.Path, "/champions/103.js") {
				detailRequests.Add(1)
				fmt.Fprint(w, `export default {"champion":{"id":103}}`)
			} else {
				http.NotFound(w, r)
			}
		}
	}))
	defer server.Close()
	source := server.URL + "/toy/resg/index.html"
	first, err := FetchKiwiBalance(context.Background(), server.Client(), source, KiwiSnapshot{})
	if err != nil {
		t.Fatal(err)
	}
	if first.Version != "16.19" || len(first.Balance) != 3 || first.Revision != server.URL+"/toy/resg/revision-2/" || detailRequests.Load() != 3 {
		t.Fatal("current revision or full hero snapshot missing", first)
	}
	same, err := FetchKiwiBalance(context.Background(), server.Client(), source, first)
	if err != nil || same.Version != first.Version || detailRequests.Load() != 3 {
		t.Fatal("unchanged data repeatedly downloaded full hero detail files", err)
	}
	newer.Store(true)
	failed.Store(true)
	if _, err := FetchKiwiBalance(context.Background(), server.Client(), source, first); err == nil {
		t.Fatal("partial dataset accepted after hero request failed")
	}
	if first.Version != "16.19" || len(first.Balance) != 3 || first.Balance["147"].Adjustments[0].Value != .9 {
		t.Fatal("failed update mutated the last good snapshot")
	}
	failed.Store(false)
	updated, err := FetchKiwiBalance(context.Background(), server.Client(), source, first)
	if err != nil || updated.Version != "16.20" || updated.Cached || len(updated.Balance) != 3 {
		t.Fatal("new complete dataset was not applied", err)
	}
}
