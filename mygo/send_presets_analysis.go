package main

import (
	"context"
	"encoding/json"
	"math"
	"reflect"
	"strconv"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

// Fallback uses retained SGP/LCU summaries only; missing challenge/timeline
// fields remain unavailable instead of becoming invented zero-valued records.
func presetAnalyzeHistory(data object, puuid string) object {
	history := presetObject(presetObject(data["matchHistory"])[puuid])
	var rows []any
	encoded, _ := json.Marshal(history["data"])
	_ = json.Unmarshal(encoded, &rows)
	games := []presetPreparedGame{}
	for _, row := range rows {
		wrapped := presetObject(row)
		g := presetObject(wrapped["data"])
		sgp := client.String(wrapped["source"]) == "sgp"
		if sgp {
			g = presetObject(g["json"])
		}
		mode := client.String(g["gameMode"])
		kind := client.String(g["gameType"])
		if (kind != "" && kind != "MATCHED_GAME") || presetPveQueue(int(presetN(g["queueId"]))) {
			continue
		}
		participants := client.List(g["participants"])
		identity := map[int]string{}
		for _, entry := range client.List(g["participantIdentities"]) {
			v := presetObject(entry)
			identity[int(presetN(v["participantId"]))] = client.String(presetPath(v, "player", "puuid"))
		}
		found := object{}
		for _, entry := range participants {
			v := presetObject(entry)
			id := client.String(v["puuid"])
			if id == "" {
				id = identity[int(presetN(v["participantId"]))]
			}
			if id == puuid {
				found = v
				break
			}
		}
		if len(found) == 0 {
			continue
		}
		s := found
		if !sgp {
			s = presetObject(found["stats"])
		}
		if s["gameEndedInEarlySurrender"] == true || s["gameEndedInEarlySurrender"] == "true" || strings.HasPrefix(client.String(g["endOfGameResult"]), "Abort_") {
			continue
		}
		team := presetN(found["teamId"])
		if mode == "CHERRY" && found["playerSubteamId"] != nil {
			team = presetN(found["playerSubteamId"])
		}
		totals := object{}
		teamCount := 0
		for _, entry := range participants {
			v := presetObject(entry)
			side := presetN(v["teamId"])
			if mode == "CHERRY" && v["playerSubteamId"] != nil {
				side = presetN(v["playerSubteamId"])
			}
			if side != team {
				continue
			}
			teamCount++
			ts := v
			if !sgp {
				ts = presetObject(v["stats"])
			}
			for _, key := range []string{"totalDamageDealtToChampions", "totalDamageTaken", "goldEarned", "kills", "visionScore", "totalMinionsKilled", "neutralMinionsKilled"} {
				totals[key] = presetN(totals[key]) + presetN(ts[key])
			}
		}
		metrics := object{"kills": s["kills"], "deaths": s["deaths"], "assists": s["assists"], "avgVisionScore": presetN(s["visionScore"])}
		metrics["avgChampionDamagePercentageOfTeam"] = presetN(s["totalDamageDealtToChampions"]) / math.Max(1, presetN(totals["totalDamageDealtToChampions"]))
		metrics["avgDamageTakenPercentageOfTeam"] = presetN(s["totalDamageTaken"]) / math.Max(1, presetN(totals["totalDamageTaken"]))
		metrics["avgGoldPercentageOfTeam"] = presetN(s["goldEarned"]) / math.Max(1, presetN(totals["goldEarned"]))
		metrics["avgCsPerMinute"] = (presetN(s["totalMinionsKilled"]) + presetN(s["neutralMinionsKilled"])) / math.Max(1, presetN(g["gameDuration"])/60)
		metrics["avgCsPercentageOfTeam"] = (presetN(s["totalMinionsKilled"]) + presetN(s["neutralMinionsKilled"])) / math.Max(1, presetN(totals["totalMinionsKilled"])+presetN(totals["neutralMinionsKilled"]))
		metrics["avgKillParticipation"] = (presetN(s["kills"]) + presetN(s["assists"])) / math.Max(1, presetN(totals["kills"]))
		metrics["avgDamageGoldEfficiency"] = presetN(s["totalDamageDealtToChampions"]) / math.Max(1, presetN(s["goldEarned"]))
		metrics["avgSoloKills"] = presetPath(s, "challenges", "soloKills")
		metrics["avgEnemyMissingPings"] = s["enemyMissingPings"]
		metrics["avgVisionScorePercentageOfTeam"] = presetN(s["visionScore"]) / math.Max(1, presetN(totals["visionScore"]))
		metrics["avgKillDamageEfficiency"] = 1.0
		if presetN(totals["kills"]) > 0 && presetN(totals["totalDamageDealtToChampions"]) > 0 {
			metrics["avgKillDamageEfficiency"] = presetN(s["kills"]) / presetN(totals["kills"]) / math.Max(.0000001, presetN(metrics["avgChampionDamagePercentageOfTeam"]))
		}
		metrics["teamCount"] = teamCount
		win := (s["win"] == true || s["win"] == "Win") && s["teamEarlySurrendered"] != true
		position := ""
		if sgp {
			position = client.String(found["teamPosition"])
		}
		placement := presetN(s["subteamPlacement"])
		subteams := map[int]bool{}
		if mode == "CHERRY" {
			for _, entry := range participants {
				v := presetObject(entry)
				stats := v
				if !sgp {
					stats = presetObject(v["stats"])
				}
				id := int(presetN(stats["playerSubteamId"]))
				if id > 0 && presetN(stats["subteamPlacement"]) > 0 {
					subteams[id] = true
				}
			}
		}
		spell1, spell2 := int(presetN(found["spell1Id"])), int(presetN(found["spell2Id"]))
		if found["summoner1Id"] != nil {
			spell1, spell2 = int(presetN(found["summoner1Id"])), int(presetN(found["summoner2Id"]))
		}
		games = append(games, presetPreparedGame{champion: int(presetN(found["championId"])), position: position, win: win, metrics: metrics, spell1: spell1, spell2: spell2, creation: presetN(g["gameCreation"]), duration: presetN(g["gameDuration"]), mode: mode, team: int(presetN(found["teamId"])), placement: placement, subteamCount: len(subteams)})
	}
	if len(games) == 0 {
		return object{}
	}
	a := presetAggregateGames(games)
	champions := object{}
	byChampion := map[int][]presetPreparedGame{}
	for _, g := range games {
		byChampion[g.champion] = append(byChampion[g.champion], g)
	}
	for id, entries := range byChampion {
		ch := presetAggregateGames(entries)
		ch["championId"] = id
		champions[strconv.Itoa(id)] = ch
	}
	a["champions"] = champions
	presetAddGameBreakdown(a, games)
	return a
}

type presetPreparedGame struct {
	champion           int
	position           string
	win                bool
	metrics            object
	spell1, spell2     int
	creation, duration float64
	mode               string
	team, subteamCount int
	placement          float64
}

func presetAggregateGames(games []presetPreparedGame) object {
	summary := object{}
	positions := object{}
	wins := 0
	allSolo := true
	for _, g := range games {
		if g.win {
			wins++
		}
		if g.position != "" {
			positions[strings.ToUpper(g.position)] = presetN(positions[strings.ToUpper(g.position)]) + 1
		}
		for _, key := range []string{"kills", "deaths", "assists", "avgVisionScore", "avgChampionDamagePercentageOfTeam", "avgDamageTakenPercentageOfTeam", "avgGoldPercentageOfTeam", "avgCsPerMinute", "avgCsPercentageOfTeam", "avgKillParticipation", "avgDamageGoldEfficiency", "avgSoloKills", "avgVisionScorePercentageOfTeam", "avgKillDamageEfficiency", "avgEnemyMissingPings"} {
			if _, ok := presetNumber(g.metrics[key]); !ok && key == "avgSoloKills" {
				allSolo = false
			}
			summary[key] = presetN(summary[key]) + presetN(g.metrics[key])
		}
	}
	summary["avgKda"] = (presetN(summary["kills"]) + presetN(summary["assists"])) / math.Max(1, presetN(summary["deaths"]))
	for key, value := range summary {
		if strings.HasPrefix(key, "avg") && key != "avgKda" {
			summary[key] = presetN(value) / float64(len(games))
		}
	}
	if !allSolo {
		summary["avgSoloKills"] = nil
	}
	var positionValue any = positions
	if games[0].position == "" {
		positionValue = nil
	}
	missingPings := false
	for _, g := range games {
		if _, ok := presetNumber(g.metrics["avgEnemyMissingPings"]); !ok {
			missingPings = true
		}
	}
	if missingPings {
		summary["avgEnemyMissingPings"] = nil
	}
	winning, losing, flashD, flashF := 0, 0, 0, 0
	streakOpen := true
	for i, g := range games {
		if g.spell1 == 4 {
			flashD++
		}
		if g.spell2 == 4 {
			flashF++
		}
		if i > 0 && games[i-1].win != g.win {
			streakOpen = false
		}
		if streakOpen {
			if g.win && losing == 0 {
				winning++
			} else if !g.win && winning == 0 {
				losing++
			}
		}
	}
	activeWins, activeLosses := 0, 0
	ended := games[0].creation + games[0].duration*1000
	if float64(time.Now().UnixMilli())-ended < 4*60*60*1000 {
		for i, g := range games {
			if i > 0 && ended-g.creation > 8*60*60*1000 {
				break
			}
			if g.win {
				activeWins++
			} else {
				activeLosses++
			}
			ended = g.creation + g.duration*1000
		}
	}
	winLoss := object{"count": len(games), "wins": wins, "losses": len(games) - wins, "winRate": float64(wins) / float64(len(games)), "winningStreak": winning, "losingStreak": losing, "activeSessionWins": activeWins, "activeSessionLosses": activeLosses}
	summary["winRate"] = winLoss["winRate"]
	return object{"count": len(games), "summary": summary, "positions": positionValue, "winLoss": object{"all": winLoss}, "jungle": nil, "details": nil, "spells": object{"flashOnD": flashD, "flashOnF": flashF}, "akariScore": presetAkariScore(summary, games)}
}
func (d *Desktop) generatePreset(ctx context.Context, kind, target string) ([]string, error) {
	ctx, release := d.client.RequestScope(ctx)
	defer release()
	view := presetObject(d.client.State())
	gameData := presetObject(view["gameData"])
	c := sendPresetContext{state: presetObject(d.game.State()), data: presetObject(d.game.GetAll()), champions: presetObject(gameData["champions"]), self: client.String(presetPath(view, "summoner", "me", "puuid")), target: target, locale: client.String(d.settingValue("app-common-main", "locale"))}
	if c.locale == "" {
		c.locale = client.String(d.settingValue("app-common-main", "language"))
	}
	key := strings.ToLower(kind) + "Puuids"
	if kind == "Premade" {
		key = "premadeIndices"
	}
	selection := d.state("in-game-send-main", "state")[key]
	options := presetObject(d.settingValue("in-game-send-main", lowerFirst(kind)+"PresetOptions"))
	if kind == "Jungle" {
		if err := d.preparePresetJungle(ctx, &c, options, selection); err != nil {
			return nil, err
		}
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	current := d.game.State()
	for _, key := range []string{"teams", "queryStage", "draft"} {
		if !reflect.DeepEqual(c.state[key], current[key]) {
			return nil, context.Canceled
		}
	}
	return c.lines(kind, options, selection), nil
}
func presetPveQueue(id int) bool {
	switch id {
	case 31, 32, 33, 34, 35, 36, 52, 800, 801, 810, 820, 830, 831, 832, 840, 841, 842, 850, 851, 852, 860, 870, 880, 890, 2000, 2010, 2020, 90, 91, 92, 950, 951, 960, 961, 981, 982, 990, 1030, 1031, 1032, 1040, 1041, 1050, 1051, 1060, 1061, 1070, 1071, 1800, 1810, 1820, 1830, 1840, 1850, 1860, 1870, 1880, 1890:
		return true
	}
	return false
}
func presetScore(value, min, max, weight float64) float64 {
	return (math.Max(min, math.Min(max, value)) - min) / (max - min) * weight
}
func presetAkariScore(summary object, games []presetPreparedGame) object {
	score := object{"kdaScore": math.Min(1, math.Sqrt(presetN(summary["avgKda"]))*3/7), "winRateScore": presetScore(presetN(summary["winRate"]), .5, 1, 1)}
	for _, key := range []string{"dmgScore", "dmgTakenScore", "csScore", "goldScore", "participationScore", "visionScore"} {
		score[key] = 0.0
	}
	for _, g := range games {
		size := presetN(g.metrics["teamCount"])
		expected := func(key string) float64 {
			if size <= 1 {
				return 0
			}
			return presetN(g.metrics[key]) * size
		}
		values := object{"dmgScore": presetScore(expected("avgChampionDamagePercentageOfTeam"), 1, 2, 3), "dmgTakenScore": presetScore(expected("avgDamageTakenPercentageOfTeam"), 1, 2, 2), "csScore": presetScore(presetN(g.metrics["avgCsPerMinute"]), 5, 10, 2), "goldScore": presetScore(expected("avgGoldPercentageOfTeam"), 1, 1.5, 2), "participationScore": presetScore(presetN(g.metrics["avgKillParticipation"]), .3, 1, 2), "visionScore": presetScore(expected("avgVisionScorePercentageOfTeam"), 1, 2, 2)}
		for key, value := range values {
			score[key] = presetN(score[key]) + presetN(value)/float64(len(games))
		}
	}
	total := 0.0
	for _, value := range score {
		total += presetN(value)
	}
	score["total"] = total
	score["outstanding"] = total >= 6.5 && len(games) >= 5
	score["extraordinary"] = total >= 8 && len(games) >= 8
	return score
}
func presetAddGameBreakdown(a object, games []presetPreparedGame) {
	normal, cherry := []presetPreparedGame{}, []presetPreparedGame{}
	blue, red, top1, topHalf, placementCount := 0, 0, 0, 0, 0
	placementSum := 0.0
	for _, g := range games {
		if g.mode == "CHERRY" {
			cherry = append(cherry, g)
			if g.placement == 1 {
				top1++
			}
			if g.placement > 0 {
				placementCount++
				placementSum += g.placement
				if g.subteamCount > 0 && g.placement <= math.Floor(float64(g.subteamCount)/2) {
					topHalf++
				}
			}
		} else {
			normal = append(normal, g)
			if g.team == 100 {
				blue++
			}
			if g.team == 200 {
				red++
			}
		}
	}
	winLoss := presetObject(a["winLoss"])
	empty := object{"count": 0, "wins": 0, "losses": 0, "winRate": 0.0, "winningStreak": 0, "losingStreak": 0, "activeSessionWins": 0, "activeSessionLosses": 0}
	n := empty
	if len(normal) > 0 {
		n = presetObject(presetPath(presetAggregateGames(normal), "winLoss", "all"))
	}
	ch := presetObject(client.Clone(empty))
	if len(cherry) > 0 {
		ch = presetObject(presetPath(presetAggregateGames(cherry), "winLoss", "all"))
	}
	den := math.Max(1, float64(len(cherry)))
	ch["top1s"] = top1
	ch["topHalfFinishes"] = topHalf
	ch["top1Rate"] = float64(top1) / den
	ch["topHalfRate"] = float64(topHalf) / den
	ch["avgSubteamPlacement"] = placementSum / math.Max(1, float64(placementCount))
	winLoss["normal"] = n
	winLoss["cherry"] = ch
	a["teamSide"] = object{"blueSideCount": blue, "redSideCount": red}
	for id, entry := range presetObject(a["champions"]) {
		filtered := []presetPreparedGame{}
		for _, g := range games {
			if strconv.Itoa(g.champion) == id {
				filtered = append(filtered, g)
			}
		}
		if len(filtered) > 0 {
			presetAddGameBreakdown(presetObject(entry), filtered)
		}
	}
}
