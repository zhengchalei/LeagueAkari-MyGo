package catalog

import (
	"context"
	"fmt"
	"html"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
)

const championFixture = `-- Module:ChampionData/data
return {
 ["Ahri"] = {
   ["id"] = 103,
   ["name"] = "阿狸",
   ["description"] = [[An unrelated long description]],
   ["stats"] = {
     ["aram"] = { ["dmg_dealt"] = 1, ["dmg_taken"] = 0.95, ["tenacity"] = 1, ["ability_haste"] = -5 },
     ["urf"] = { ["dmg_dealt"] = 0.90, ["healing"] = .85, ["mana_regen"] = 2e0 },
     ["kiwi"] = { ["dmg_dealt"] = 1.25 },
     ["normal"] = { ["attack_speed"] = 1.4 },
   },
 },
 ['Seraphine'] = {
   id = 147,
   notes = "do not parse -- this as a comment",
   stats = { ar = { shielding = 0.8, healing = 0.7 }, nb = { ability_haste = +10 }, ofa = { movement_speed = -15 }, usb = { energy_regen = 1.2 } },
 },
 ["Unchanged"] = { id=999, stats={ aram={dmg_dealt=1,dmg_taken=1,tenacity=1} } },
 ["NoStats"] = { id=1000 },
}
`

func TestParseBalancePreservesOriginalModesAndSignedNumericFields(t *testing.T) {
	balance, err := ParseBalance(championFixture)
	if err != nil {
		t.Fatal(err)
	}
	if len(balance) != 2 || balance["103"].ID != 103 || balance["147"].ID != 147 {
		t.Fatalf("champion balances missing: %+v", balance)
	}
	expected := map[string]Balance{"aram": {"dmg_taken": 0.95, "ability_haste": -5}, "urf": {"dmg_dealt": 0.9, "healing": 0.85, "mana_regen": 2}}
	if !reflect.DeepEqual(balance["103"].Balance, expected) {
		t.Fatalf("schema/default filtering/mode isolation changed: %+v", balance["103"])
	}
	if balance["147"].Balance["nb"]["ability_haste"] != 10 || balance["147"].Balance["ofa"]["movement_speed"] != -15 || balance["147"].Balance["usb"]["energy_regen"] != 1.2 {
		t.Fatal("signed mode stats were lost")
	}
	if _, exists := balance["103"].Balance["kiwi"]; exists {
		t.Fatal("legacy Fandom stats must not masquerade as independent KIWI balance")
	}
}

func TestParseBalanceExtractsAndUnescapesHTMLPre(t *testing.T) {
	page := `<!doctype html><html><pre class="unrelated">wrong</pre><pre dir='ltr' class='mw-code mw-script'>` + html.EscapeString(championFixture) + `</pre></html>`
	parsed, err := ParseBalance(page)
	if err != nil {
		t.Fatal(err)
	}
	expected, err := ParseBalance(championFixture)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(parsed, expected) {
		t.Fatal("HTML Lua source differs from raw source")
	}
}

func TestEmptyMalformedOrNeutralOnlyBalanceIsAnError(t *testing.T) {
	for _, source := range []string{"", `return {}`, `<html><title>Forbidden</title></html>`, `return { ["Hero"] = {id=1,stats={aram={dmg_dealt=1,dmg_taken=1,tenacity=1}}} }`, `return { ["Hero"] = {id=1,stats={aram={ability_haste=-5}}}`, `return { ["Hero"] = {id=1,stats={kiwi={dmg_dealt=1.5}}} }`} {
		if _, err := ParseBalance(source); err == nil {
			t.Fatalf("invalid/empty data accepted: %.80s", source)
		}
	}
}

func TestFetchBalanceUsesRawFallbackAndRetainsCachedDataOnFailure(t *testing.T) {
	failed := false
	htmlCalls, rawCalls := 0, 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if failed {
			w.WriteHeader(503)
			return
		}
		if r.URL.Query().Get("action") == "raw" {
			rawCalls++
			fmt.Fprint(w, championFixture)
		} else {
			htmlCalls++
			fmt.Fprint(w, `<html>source page without pre</html>`)
		}
	}))
	defer server.Close()
	service := New(server.URL + "/wiki/Module:ChampionData/data")
	result, err := service.FetchBalance(context.Background(), server.Client())
	if err != nil {
		t.Fatal(err)
	}
	if len(result) != 2 || htmlCalls != 1 || rawCalls != 1 {
		t.Fatal("HTML-to-raw fallback did not return real nonempty data")
	}
	result["103"].Balance["aram"]["ability_haste"] = 100
	if service.Snapshot()["103"].Balance["aram"]["ability_haste"] != -5 {
		t.Fatal("returned result mutated cached data")
	}
	failed = true
	if result, err := service.FetchBalance(context.Background(), server.Client()); err == nil || result != nil {
		t.Fatal("unavailable external site reported successful replacement")
	}
	if len(service.Snapshot()) != 2 {
		t.Fatal("last good cache cleared by unavailable source")
	}
}

func TestLuaLexerLongCommentsEscapedStringsAndUnrelatedValues(t *testing.T) {
	source := `--[=[ comment with return {} and -- ]=]
return {
 ["A\\B\"C"]={id=1,display="line\nwith\tspaces",description=[=[text with ] brackets]=],unknown=someFunction("arg"),stats={urf={dmg_taken=1.1,attack_speed=-1e-1}}},
}`
	result, err := ParseBalance(source)
	if err != nil {
		t.Fatal(err)
	}
	if result["1"].Balance["urf"]["attack_speed"] != -0.1 {
		t.Fatal("exponent/signed field lost")
	}
	if _, err := ParseBalance(strings.Replace(source, `attack_speed=-1e-1`, `attack_speed=-unknown`, 1)); err == nil {
		t.Fatal("non-numeric signed balance was silently accepted")
	}
}
