package main

import (
	_ "embed"
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

// The original League Akari preset vocabulary is kept in both supported locales.
//
//go:embed send_presets_i18n.json
var presetTranslationsJSON []byte
var presetTranslations = func() map[string]map[string]string {
	var v map[string]map[string]string
	_ = json.Unmarshal(presetTranslationsJSON, &v)
	return v
}()

type sendPresetContext struct {
	state, data, champions object
	self, target, locale   string
}
type sendPresetPlayer struct {
	puuid, name, tag string
	champion, group  int
	analysis         object
}
type sendPresetTeam struct {
	id       string
	friendly bool
	players  []sendPresetPlayer
}

func presetObject(v any) object {
	switch v := v.(type) {
	case json.RawMessage:
		var out object
		_ = json.Unmarshal(v, &out)
		return out
	case object:
		return v
	}
	return object{}
}
func presetNumber(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, !math.IsNaN(n) && !math.IsInf(n, 0)
	case int:
		return float64(n), true
	case int64:
		return float64(n), true
	case json.Number:
		f, e := n.Float64()
		return f, e == nil
	}
	return 0, false
}
func presetN(v any) float64 { n, _ := presetNumber(v); return n }
func presetPath(v object, keys ...string) any {
	var value any = v
	for _, k := range keys {
		value = presetObject(value)[k]
	}
	return value
}
func (c sendPresetContext) t(key string, values ...any) string {
	lang := c.locale
	if lang != "en" {
		lang = "zh-CN"
	}
	table := presetTranslations[lang]
	if table[key] == "" && len(values) >= 2 && values[0] == "count" {
		suffix := "_other"
		if presetN(values[1]) == 1 {
			suffix = "_one"
		}
		key += suffix
	}
	s := table[key]
	if s == "" {
		s = key
	}
	for i := 0; i+1 < len(values); i += 2 {
		s = strings.ReplaceAll(s, "{{"+fmt.Sprint(values[i])+"}}", fmt.Sprint(values[i+1]))
	}
	return s
}
func presetFixed(v any, digits int) string {
	if n, ok := presetNumber(v); ok {
		return strconv.FormatFloat(n, 'f', digits, 64)
	}
	return "-"
}
func presetRate(v any) string {
	if n, ok := presetNumber(v); ok {
		return strconv.FormatFloat(math.Floor(n*100+0.5), 'f', 0, 64) + "%"
	}
	return "-"
}
func (c sendPresetContext) championName(id int) string {
	name := client.String(presetObject(c.champions[strconv.Itoa(id)])["name"])
	if name == "" {
		return strconv.Itoa(id)
	}
	return name
}
func presetTeamOrder(id string) int {
	if id == "TEAM-100" {
		return 100
	}
	if id == "TEAM-200" {
		return 200
	}
	if strings.HasPrefix(id, "CHERRY-") {
		n, e := strconv.Atoi(strings.TrimPrefix(id, "CHERRY-"))
		if e == nil {
			return 1000 + n
		}
	}
	return math.MaxInt
}
func (c sendPresetContext) teams() []sendPresetTeam {
	teams := []sendPresetTeam{}
	summoners := presetObject(c.data["summoner"])
	if len(summoners) == 0 {
		summoners = presetObject(c.state["summoner"])
	}
	analysis := presetObject(presetPath(c.state, "analysis", "players"))
	for id, members := range presetObject(c.state["teams"]) {
		team := sendPresetTeam{id: id}
		for _, entry := range client.List(members) {
			id := client.String(entry)
			s := presetObject(summoners[id])
			name := client.String(s["gameName"])
			if name == "" {
				name = client.String(s["displayName"])
			}
			if name == "" {
				name = id
				if len(name) > 6 {
					name = name[:6]
				}
			}
			a := presetObject(analysis[id])
			if len(a) == 0 {
				a = presetAnalyzeHistory(c.data, id)
			}
			team.players = append(team.players, sendPresetPlayer{puuid: id, name: name, tag: client.String(s["tagLine"]), champion: int(presetN(presetObject(c.state["championSelections"])[id])), group: int(presetN(presetObject(c.state["mergedPremadeTeamMap"])[id])), analysis: a})
			if id == c.self {
				team.friendly = true
			}
		}
		if len(team.players) > 0 {
			teams = append(teams, team)
		}
	}
	sort.SliceStable(teams, func(i, j int) bool {
		if teams[i].friendly != teams[j].friendly {
			return teams[i].friendly
		}
		a, b := presetTeamOrder(teams[i].id), presetTeamOrder(teams[j].id)
		if a != b {
			return a < b
		}
		return teams[i].id < teams[j].id
	})
	if c.target == "all" {
		return teams
	}
	if len(teams) == 0 {
		return teams
	}
	if c.target == "friendly" {
		return teams[:1]
	}
	return teams[1:]
}
func presetSelectedPlayers(teams []sendPresetTeam, selected any) []sendPresetPlayer {
	wanted := map[string]bool{}
	for _, v := range client.List(selected) {
		wanted[client.String(v)] = true
	}
	out := []sendPresetPlayer{}
	for _, t := range teams {
		for _, p := range t.players {
			if wanted[p.puuid] {
				out = append(out, p)
			}
		}
	}
	return out
}
func presetChampionCounts(players []sendPresetPlayer) map[int]int {
	out := map[int]int{}
	for _, p := range players {
		if p.champion > 0 {
			out[p.champion]++
		}
	}
	return out
}
func (c sendPresetContext) display(p sendPresetPlayer, strategy string, counts map[int]int) (string, bool) {
	name := p.name
	if p.tag != "" {
		name += "#" + p.tag
	}
	uses := p.champion > 0 && (strategy == "championNameWithName" || (strategy == "preferChampionName" && counts[p.champion] == 1))
	if uses {
		champ := c.championName(p.champion)
		if strategy == "championNameWithName" {
			return c.t("common.championWithPlayer", "champion", champ, "player", name), true
		}
		return champ, true
	}
	return name, false
}

type presetMainItem struct {
	id    int
	key   string
	count float64
}

func presetDominant(items []presetMainItem, max int) []presetMainItem {
	if len(items) <= max {
		return items
	}
	for i := max - 1; i >= 0; i-- {
		if items[i].count >= items[i+1].count*1.5 {
			return items[:i+1]
		}
	}
	return nil
}
func (c sendPresetContext) mainChampions(a object, jungle bool) []string {
	items := []presetMainItem{}
	for _, v := range presetObject(a["champions"]) {
		ch := presetObject(v)
		count := presetN(presetPath(ch, "winLoss", "all", "count"))
		if jungle {
			count = presetN(presetPath(ch, "jungle", "gamesAnalyzed"))
		}
		id := int(presetN(ch["championId"]))
		if id > 0 && count >= 2 {
			items = append(items, presetMainItem{id: id, count: count})
		}
	}
	sort.SliceStable(items, func(i, j int) bool {
		if items[i].count != items[j].count {
			return items[i].count > items[j].count
		}
		return items[i].id < items[j].id
	})
	names := []string{}
	for _, it := range presetDominant(items, 3) {
		names = append(names, c.championName(it.id))
	}
	return names
}

var presetPositions = []string{"TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"}

func (c sendPresetContext) rating(p sendPresetPlayer, opts object, usesChampion bool) string {
	hero := opts["showCurrentChampion"] == true
	if hero && p.champion <= 0 {
		return c.t("common.noChampionSelected")
	}
	a := p.analysis
	subject := ""
	if hero {
		subject = c.championName(p.champion)
		if usesChampion {
			subject = c.t("common.thisChampion")
		}
		a = presetObject(presetObject(a["champions"])[strconv.Itoa(p.champion)])
	}
	none := c.t("rating.noRecentMatches")
	if hero {
		none = c.t("rating.noChampionMatches", "champion", subject)
	}
	if len(a) == 0 {
		return none
	}
	count := a["count"]
	if hero {
		count = presetPath(a, "winLoss", "all", "count")
	}
	if n, ok := presetNumber(count); ok && n <= 0 {
		return none
	}
	summary := presetObject(a["summary"])
	parts := []string{}
	push := func(key, value string) { parts = append(parts, c.t("rating.metrics."+key, "value", value)) }
	if opts["winRate"] == true {
		push("winRate", presetRate(presetPath(a, "winLoss", "all", "winRate")))
	}
	if opts["kda"] == true {
		push("kda", presetFixed(summary["avgKda"], 2))
	}
	for _, key := range []string{"avgSoloKills", "avgVisionScore", "avgChampionDamage", "avgDamageTaken", "avgGold", "avgCsPerMinute", "avgKillParticipation", "avgDamageGoldEfficiency"} {
		if opts[key] != true || len(summary) == 0 {
			continue
		}
		field := key
		rate := false
		switch key {
		case "avgChampionDamage":
			field = "avgChampionDamagePercentageOfTeam"
			rate = true
		case "avgDamageTaken":
			field = "avgDamageTakenPercentageOfTeam"
			rate = true
		case "avgGold":
			field = "avgGoldPercentageOfTeam"
			rate = true
		case "avgKillParticipation", "avgDamageGoldEfficiency":
			rate = true
		}
		if key == "avgSoloKills" {
			if _, ok := presetNumber(summary[field]); !ok {
				continue
			}
		}
		value := presetFixed(summary[field], 1)
		if rate {
			value = presetRate(summary[field])
		}
		push(key, value)
	}
	if opts["mainChampions"] == true && !hero {
		names := c.mainChampions(p.analysis, false)
		if len(names) == 0 {
			parts = append(parts, c.t("common.noMainChampions"))
		} else {
			parts = append(parts, c.t("rating.metrics.mainChampions", "champions", strings.Join(names, c.t("punctuation.listSeparator"))))
		}
	}
	if opts["mainPositions"] == true {
		items := []presetMainItem{}
		for i, pos := range presetPositions {
			if n := presetN(presetObject(a["positions"])[pos]); n > 0 {
				items = append(items, presetMainItem{id: i, key: pos, count: n})
			}
		}
		sort.SliceStable(items, func(i, j int) bool { return items[i].count > items[j].count })
		if !hero {
			items = presetDominant(items, 2)
		}
		names := []string{}
		for _, it := range items {
			names = append(names, c.t("positions."+it.key))
		}
		if len(names) > 0 {
			parts = append(parts, c.t("rating.metrics.mainPositions", "positions", strings.Join(names, c.t("punctuation.listSeparator"))))
		}
	}
	if n, ok := presetNumber(count); ok {
		key := "rating.matchCount"
		values := []any{"count", n}
		if hero {
			key = "rating.championMatchCount"
			values = append(values, "champion", subject)
		}
		prefix := c.t(key, values...)
		if len(parts) > 0 {
			return c.t("rating.countWithStats", "countText", prefix, "stats", strings.Join(parts, " "))
		}
		return prefix
	}
	if len(parts) > 0 {
		return strings.Join(parts, " ")
	}
	return none
}
func (c sendPresetContext) jungle(p sendPresetPlayer, opts object, usesChampion bool) string {
	hero := opts["showCurrentChampion"] == true
	if hero && p.champion <= 0 {
		return c.t("common.noChampionSelected")
	}
	a := p.analysis
	subject := c.championName(p.champion)
	if usesChampion {
		subject = c.t("common.thisChampion")
	}
	if hero {
		a = presetObject(presetObject(a["champions"])[strconv.Itoa(p.champion)])
	}
	j := presetObject(a["jungle"])
	if len(j) == 0 {
		if hero {
			return c.t("jungle.noChampionRecords", "champion", subject)
		}
		return c.t("jungle.noRecords")
	}
	prefix := "jungle.sampleCount"
	if hero {
		prefix = "jungle.championSampleCount"
	}
	parts := []string{c.t(prefix, "count", presetN(j["gamesAnalyzed"]), "champion", subject)}
	if opts["activityPreference"] == true {
		lanes := []presetMainItem{{key: "top", count: presetN(j["avgTopZonePercentage"])}, {key: "mid", count: presetN(j["avgMidZonePercentage"])}, {key: "bot", count: presetN(j["avgBotZonePercentage"])}}
		sort.SliceStable(lanes, func(i, k int) bool { return lanes[i].count > lanes[k].count })
		pref := c.t("jungle.lanePreference.unclear")
		if lanes[0].count > 0 {
			if lanes[1].count >= lanes[0].count*.65 {
				pref = c.t("jungle.lanePreference.double", "first", c.t("jungle.lanes."+lanes[0].key), "second", c.t("jungle.lanes."+lanes[1].key))
			} else {
				pref = c.t("jungle.lanePreference.single", "lane", c.t("jungle.lanes."+lanes[0].key))
			}
		}
		parts = append(parts, c.t("jungle.metrics.activityPreference", "preference", pref, "top", presetRate(j["avgTopZonePercentage"]), "mid", presetRate(j["avgMidZonePercentage"]), "bot", presetRate(j["avgBotZonePercentage"])))
	}
	if hero && opts["firstClearDistribution"] == true {
		fc := presetObject(j["firstClearCamp"])
		for _, side := range []string{"blue", "red"} {
			for _, kind := range []string{"normal", "invade"} {
				record := side
				if kind == "invade" {
					record += "Invade"
				}
				campItems := []presetMainItem{}
				total := 0.0
				for _, camp := range []string{"blue", "red", "wolves", "raptors"} {
					n := presetN(presetObject(fc[record])[camp])
					total += n
					if n > 0 {
						campItems = append(campItems, presetMainItem{key: camp, count: n})
					}
				}
				rate := "-"
				games := presetN(fc[side+"Games"])
				distribution := ""
				if games > 0 {
					rate = presetRate(total / games)
					if total > 0 {
						sort.SliceStable(campItems, func(i, j int) bool { return campItems[i].count > campItems[j].count })
						names := []string{}
						for _, camp := range campItems {
							names = append(names, c.t("jungle.campRate", "camp", c.t("jungle.camps."+camp.key), "rate", presetRate(camp.count/total)))
						}
						distribution = "[" + strings.Join(names, c.t("punctuation.listSeparator")) + "]"
					}
				}
				parts = append(parts, c.t("jungle.firstClear.pattern", "side", c.t("jungle.firstClear."+side), "kind", c.t("jungle.firstClear."+kind), "rate", rate, "distribution", distribution))
			}
		}
	}
	if opts["earlyGank"] == true {
		for _, level := range []string{"level3Gank", "level4Gank"} {
			parts = append(parts, c.t("jungle.metrics."+level, "value", presetRate(presetPath(j, "earlyGank", level+"Rate"))))
		}
	}
	o := presetObject(j["objectives"])
	if opts["dragonControl"] == true {
		tm := ""
		if n, ok := presetNumber(o["avgFirstDragonTime"]); ok {
			seconds := int(math.Floor(n + .5))
			tm = c.t("jungle.firstDragonAverageTime", "time", fmt.Sprintf("%d:%02d", seconds/60, seconds%60))
		}
		parts = append(parts, c.t("jungle.metrics.firstDragon", "value", presetRate(o["firstDragonRate"]), "time", tm), c.t("jungle.metrics.avgDragons", "value", presetFixed(o["avgDragons"], 1)))
	}
	if opts["monsterControl"] == true {
		parts = append(parts, c.t("jungle.metrics.monsterControl", "voidgrubs", presetFixed(o["avgVoidgrubs"], 1), "heralds", presetFixed(o["avgHeralds"], 1), "barons", presetFixed(o["avgBarons"], 1)))
	}
	if !hero && opts["mainChampions"] == true {
		names := c.mainChampions(p.analysis, true)
		if len(names) > 0 {
			parts = append(parts, c.t("jungle.metrics.mainChampions", "champions", strings.Join(names, c.t("punctuation.listSeparator"))))
		} else {
			parts = append(parts, c.t("common.noMainChampions"))
		}
	}
	return strings.Join(parts, " ")
}
func (c sendPresetContext) lines(kind string, opts object, selected any) []string {
	teams := c.teams()
	out := []string{}
	if kind != "Premade" {
		players := presetSelectedPlayers(teams, selected)
		counts := presetChampionCounts(players)
		for _, p := range players {
			name, uses := c.display(p, client.String(opts["nameDisplayStrategy"]), counts)
			stats := c.rating(p, opts, uses)
			if kind == "Jungle" {
				stats = c.jungle(p, opts, uses)
			}
			out = append(out, name+c.t("punctuation.lineSeparator")+stats)
		}
		return out
	}
	wanted := map[int]bool{}
	for _, v := range client.List(selected) {
		wanted[int(presetN(v))] = true
	}
	groups := make([]map[int][]sendPresetPlayer, len(teams))
	all := []sendPresetPlayer{}
	for i, t := range teams {
		groups[i] = map[int][]sendPresetPlayer{}
		for _, p := range t.players {
			if p.group > 0 && wanted[p.group] {
				groups[i][p.group] = append(groups[i][p.group], p)
			}
		}
		for id, players := range groups[i] {
			if len(players) < 2 {
				delete(groups[i], id)
			} else {
				all = append(all, players...)
			}
		}
	}
	counts := presetChampionCounts(all)
	for i, t := range teams {
		label := "premade.teamTitle"
		empty := "premade.empty"
		if t.id == "TEAM-100" {
			label = "premade.blueTeamTitle"
			empty = "premade.blueTeamEmpty"
		}
		if t.id == "TEAM-200" {
			label = "premade.redTeamTitle"
			empty = "premade.redTeamEmpty"
		}
		ids := []int{}
		for id := range groups[i] {
			ids = append(ids, id)
		}
		sort.Ints(ids)
		parts := []string{}
		for _, id := range ids {
			names := []string{}
			for _, p := range groups[i][id] {
				name, _ := c.display(p, client.String(opts["nameDisplayStrategy"]), counts)
				names = append(names, name)
			}
			parts = append(parts, "["+strings.Join(names, c.t("punctuation.memberSeparator"))+"]")
		}
		if len(parts) == 0 {
			out = append(out, c.t(empty))
		} else {
			out = append(out, c.t(label)+c.t("punctuation.lineSeparator")+strings.Join(parts, c.t("punctuation.groupSeparator")))
		}
	}
	return out
}
