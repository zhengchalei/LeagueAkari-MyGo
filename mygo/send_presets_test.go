package main

import (
	"encoding/json"
	"os"
	"reflect"
	"testing"
)

func presetFixture(t *testing.T) sendPresetContext {
	t.Helper()
	b, e := os.ReadFile("testdata/preset-original-fixture.json")
	if e != nil {
		t.Fatal(e)
	}
	var f object
	if e = json.Unmarshal(b, &f); e != nil {
		t.Fatal(e)
	}
	chs := object{}
	for id, name := range presetObject(presetPath(f, "leagueClient", "data", "gameData", "championNames")) {
		chs[id] = object{"name": name}
	}
	return sendPresetContext{state: presetObject(presetPath(f, "ongoingGame", "state")), data: object{}, champions: chs, self: "p1", target: "friendly", locale: "zh-CN"}
}
func checkPreset(t *testing.T, got []string, want ...string) {
	t.Helper()
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %q\nwant %q", got, want)
	}
}
func TestPresetsOriginalRatingFixtures(t *testing.T) {
	c := presetFixture(t)
	o := object{"kda": true, "winRate": true, "avgSoloKills": true, "mainChampions": true, "mainPositions": true, "nameDisplayStrategy": "preferChampionName"}
	checkPreset(t, c.lines("Rating", o, []any{"p1", "p2"}), "无极剑圣：50场对局，胜率59% KDA2.34 场均单杀0.8 主玩英雄[无极剑圣，盲僧，深海泰坦] 主玩位置[中路，打野]", "盲僧：近期没有对局记录")
	c.locale = "en"
	c.champions["103"] = object{"name": "Master Yi"}
	c.champions["64"] = object{"name": "Lee Sin"}
	c.champions["111"] = object{"name": "Nautilus"}
	checkPreset(t, c.lines("Rating", o, []any{"p1", "p2"}), "Master Yi: 50 matches, Win Rate 59% KDA 2.34 Avg Solo Kills 0.8 Main Champions [Master Yi, Lee Sin, Nautilus] Main Positions [Middle, Jungle]", "Lee Sin: No recent matches")
}
func TestPresetsOriginalJungleFixtures(t *testing.T) {
	c := presetFixture(t)
	o := object{"activityPreference": true, "firstClearDistribution": true, "earlyGank": true, "dragonControl": true, "monsterControl": true, "mainChampions": true, "showCurrentChampion": true, "nameDisplayStrategy": "preferChampionName"}
	checkPreset(t, c.lines("Jungle", o, []any{"p1"}), "无极剑圣：本英雄打野样本16场 前期偏中下，上22%中46%下32% 蓝方常规开75%[红Buff67%，蓝Buff33%] 蓝方入侵开25%[蓝Buff100%] 红方常规开100%[蓝Buff75%，红Buff25%] 红方入侵开0% 3级抓31% 4级抓50% 一龙率63%，首龙均时6:12 场均小龙1.9 野怪资源巢虫2.4/先锋0.5/大龙0.3")
	o["showCurrentChampion"] = false
	checkPreset(t, c.lines("Jungle", o, []any{"p1"}), "无极剑圣：打野样本20场 前期偏中下，上28%中41%下31% 3级抓24% 4级抓43% 一龙率58%，首龙均时6:30 场均小龙1.7 野怪资源巢虫2.1/先锋0.4/大龙0.2 主玩英雄[无极剑圣，盲僧，深海泰坦]")
}
func TestPresetsSelectionNamingAndPremade(t *testing.T) {
	c := presetFixture(t)
	o := object{"nameDisplayStrategy": "preferChampionName"}
	checkPreset(t, c.lines("Premade", o, []any{1, 2}), "蓝方开黑：[无极剑圣, 盲僧] [深海泰坦, 暴走萝莉]")
	presetObject(c.state["championSelections"])["p2"] = 103
	checkPreset(t, c.lines("Premade", o, []any{1, 2}), "蓝方开黑：[RIP董事长#81406, Lee Player#002] [深海泰坦, 暴走萝莉]")
	c.state["teams"] = object{"TEAM-100": []any{"p1", "p2"}, "TEAM-200": []any{"p3", "p4"}}
	c.target = "enemy"
	checkPreset(t, c.lines("Premade", o, []any{1}), "红方无开黑")
	if len(c.lines("Rating", o, []any{"p1"})) != 0 {
		t.Fatal("enemy selection included ally")
	}
	c.target = "friendly"
	presetObject(c.state["championSelections"])["p1"] = 0
	o["showCurrentChampion"] = true
	checkPreset(t, c.lines("Rating", o, []any{"p1"}), "RIP董事长#81406：尚未选择英雄")
}
func TestPresetDominanceNullableAndPlural(t *testing.T) {
	items := []presetMainItem{{count: 5}, {count: 4}, {count: 4}, {count: 4}}
	if len(presetDominant(items, 3)) != 0 {
		t.Fatal("ambiguous usage is not main")
	}
	items[0].count = 16
	if len(presetDominant(items, 3)) != 1 {
		t.Fatal("dominance cutoff")
	}
	if presetRate(.625) != "63%" || presetFixed(nil, 2) != "-" {
		t.Fatal("rounding or nullable number")
	}
	c := presetFixture(t)
	c.locale = "en"
	if c.t("rating.matchCount", "count", 1) != "1 match" {
		t.Fatal("plural singular")
	}
}
func TestPresetSummaryFallbackActualStatistics(t *testing.T) {
	makeGame := func(deaths float64, solo any) object {
		return object{"source": "sgp", "data": object{"json": object{"gameDuration": 600, "gameMode": "CLASSIC", "participants": []any{object{"puuid": "p1", "championId": 64, "teamId": 100, "teamPosition": "JUNGLE", "kills": 10.0, "assists": 5.0, "deaths": deaths, "win": true, "totalDamageDealtToChampions": 10000.0, "totalDamageTaken": 2000.0, "goldEarned": 5000.0, "totalMinionsKilled": 20.0, "neutralMinionsKilled": 30.0, "challenges": object{"soloKills": solo}}, object{"puuid": "p2", "teamId": 100, "kills": 5.0, "totalDamageDealtToChampions": 10000.0, "totalDamageTaken": 6000.0, "goldEarned": 5000.0}}}}}
	}
	data := object{"matchHistory": object{"p1": object{"data": []any{makeGame(1, 2.0), makeGame(9, nil)}}}}
	a := presetAnalyzeHistory(data, "p1")
	s := presetObject(a["summary"])
	if presetN(s["avgKda"]) != 3 || presetN(s["avgChampionDamagePercentageOfTeam"]) != .5 || presetN(s["avgCsPerMinute"]) != 5 {
		t.Fatal("wrong actual aggregates", a)
	}
	if s["avgSoloKills"] != nil || a["jungle"] != nil {
		t.Fatal("missing fields invented")
	}
	if presetN(presetPath(a, "champions", "64", "winLoss", "all", "count")) != 2 {
		t.Fatal("champion samples lost", a)
	}
}
func TestPresetRealTimelineJungleAggregation(t *testing.T) {
	frames := []any{}
	for i := 0; i < 6; i++ {
		pf := object{"position": object{"x": 3830.0, "y": 7880.0}, "level": 3.0, "minionsKilled": 0.0, "jungleMinionsKilled": 16.0, "damageStats": object{"totalDamageDoneToChampions": float64(i) * 100}, "championStats": object{}}
		events := []any{}
		if i == 3 {
			events = append(events, object{"type": "CHAMPION_KILL", "timestamp": 170000.0, "killerId": 1.0, "position": object{"x": 7000.0, "y": 7000.0}})
		}
		if i == 5 {
			events = append(events, object{"type": "ELITE_MONSTER_KILL", "timestamp": 320000.0, "killerTeamId": 100.0, "killerId": 1.0, "monsterType": "DRAGON"}, object{"type": "ELITE_MONSTER_KILL", "timestamp": 350000.0, "killerTeamId": 100.0, "killerId": 1.0, "monsterType": "HORDE"})
		}
		frames = append(frames, object{"participantFrames": object{"1": pf}, "events": events})
	}
	sample := presetAnalyzeJungleTimeline(object{"source": "sgp", "data": object{"json": object{"frames": frames}}}, object{"participantId": 1, "teamId": 100}, 64)
	if sample == nil || sample.camp != "blue" || sample.side != "blue" || !sample.level3 || !sample.level4 || sample.total != 10 {
		t.Fatal("timeline camp/damage/kill weights", sample)
	}
	agg := presetObject(presetAggregateJungle([]*presetJungleSample{sample}))
	if presetN(presetPath(agg, "earlyGank", "byTeam", "blueGames")) != 1 || presetN(presetPath(agg, "earlyGank", "byTeam", "blueLevel3GankRate")) != 1 || presetN(agg["totalMidGanks"]) != 1 || presetN(agg["avgMidGanks"]) != 1 {
		t.Fatal("original side and gank aggregates", agg)
	}
	if presetN(agg["avgTopZonePercentage"]) != .5 || presetN(agg["avgMidZonePercentage"]) != .5 || presetN(presetPath(agg, "firstClearCamp", "blue", "blue")) != 1 || presetN(presetPath(agg, "objectives", "firstDragonRate")) != 1 || presetN(presetPath(agg, "objectives", "avgFirstDragonTime")) != 320 || presetN(presetPath(agg, "objectives", "avgVoidgrubs")) != 1 {
		t.Fatal("timeline aggregate metrics", agg)
	}
	sample = presetAnalyzeJungleTimeline(object{"source": "lcu", "data": object{"frames": frames}}, object{"participantId": 1, "teamId": 200}, 64)
	if sample.firstDragon == nil || *sample.firstDragon || sample.dragons != 0 {
		t.Fatal("enemy objective counted for self")
	}
	if presetAggregateJungle(nil) != nil {
		t.Fatal("no frames must not invent sample")
	}
}
func TestPresetEarlyDeathsAndOriginalAkariScore(t *testing.T) {
	g := object{"mapId": 11, "gameMode": "CLASSIC", "gameType": "MATCHED_GAME", "participants": []any{object{"participantId": 1, "teamId": 100}, object{"participantId": 6, "teamId": 200, "teamPosition": "JUNGLE"}}}
	p := object{"participantId": 1, "teamId": 100}
	frames := []any{object{"events": []any{object{"type": "CHAMPION_KILL", "timestamp": 800000, "victimId": 1, "killerId": 7, "assistingParticipantIds": []any{6}}, object{"type": "CHAMPION_KILL", "timestamp": 910000, "victimId": 1, "killerId": 6}}}}
	value := object{"source": "lcu", "data": object{"frames": frames}}
	if count := presetEarlyDeaths(value, g, p); count == nil || *count != 1 {
		t.Fatal("enemy jungle involvement window", count)
	}
	p["teamPosition"] = "JUNGLE"
	if presetEarlyDeaths(value, g, p) != nil {
		t.Fatal("jungler should not get easy gank stat")
	}
	metrics := object{"teamCount": 5, "avgChampionDamagePercentageOfTeam": .4, "avgDamageTakenPercentageOfTeam": .4, "avgGoldPercentageOfTeam": .3, "avgVisionScorePercentageOfTeam": .4, "avgCsPerMinute": 10, "avgKillParticipation": 1}
	games := []presetPreparedGame{}
	for i := 0; i < 8; i++ {
		games = append(games, presetPreparedGame{metrics: metrics})
	}
	score := presetAkariScore(object{"avgKda": 10, "winRate": 1}, games)
	if presetN(score["total"]) != 15 || score["outstanding"] != true || score["extraordinary"] != true {
		t.Fatal("original Akari score weighting", score)
	}
}
func TestPresetWinLossBreakdownAndStreak(t *testing.T) {
	games := []presetPreparedGame{{win: true, team: 100, mode: "CLASSIC", metrics: object{}}, {win: false, team: 200, mode: "CLASSIC", metrics: object{}}, {win: true, mode: "CHERRY", placement: 3, subteamCount: 8, metrics: object{}}, {win: true, mode: "CHERRY", placement: 1, subteamCount: 8, metrics: object{}}}
	a := presetAggregateGames(games)
	presetAddGameBreakdown(a, games)
	if presetN(presetPath(a, "winLoss", "all", "winningStreak")) != 1 || presetN(presetPath(a, "winLoss", "normal", "count")) != 2 || presetN(presetPath(a, "winLoss", "cherry", "topHalfFinishes")) != 2 || presetN(presetPath(a, "winLoss", "cherry", "top1Rate")) != .5 || presetN(presetPath(a, "teamSide", "blueSideCount")) != 1 {
		t.Fatal("original arena/team/streak contract", a)
	}
}
