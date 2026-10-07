package main

import (
	"context"
	"encoding/json"
	"fmt"
	"math"
	"strconv"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type presetJungleSample struct {
	champion, team                                                   int
	weights                                                          [3]float64
	total                                                            float64
	camp, side                                                       string
	level3, level4                                                   bool
	firstDragon                                                      *bool
	dragonTime                                                       *float64
	dragons, voidgrubs, heralds, barons                              float64
	minutePositions, gankPositions, level3Positions, level4Positions []any
	soloDragons                                                      float64
	grubTime, heraldTime, baronTime                                  *float64
}

func presetZone(x, y float64) int {
	if x < 5000 && y > 9000 {
		return 0
	}
	if x > 9000 && y < 5000 {
		return 2
	}
	if math.Abs(y-x) <= 3500 {
		return 1
	}
	if y > x {
		return 0
	}
	return 2
}
func presetTimelineFrames(value any) []any {
	o := presetObject(value)
	data := presetObject(o["data"])
	if client.String(o["source"]) == "sgp" {
		data = presetObject(data["json"])
	}
	return client.List(data["frames"])
}
func presetFramePlayer(frames []any, index, participant int) object {
	if index >= len(frames) {
		return object{}
	}
	return presetObject(presetPath(presetObject(frames[index]), "participantFrames", strconv.Itoa(participant)))
}
func presetInvolved(event object, participant int) bool {
	if int(presetN(event["killerId"])) == participant {
		return true
	}
	for _, id := range client.List(event["assistingParticipantIds"]) {
		if int(presetN(id)) == participant {
			return true
		}
	}
	return false
}
func presetAnalyzeJungleTimeline(value any, p object, champion int) *presetJungleSample {
	frames := presetTimelineFrames(value)
	if len(frames) == 0 {
		return nil
	}
	participant := int(presetN(p["participantId"]))
	if participant <= 0 {
		return nil
	}
	sample := &presetJungleSample{champion: champion, team: int(presetN(p["teamId"]))}
	for i := 1; i < len(frames) && i < 15; i++ {
		pos := presetObject(presetFramePlayer(frames, i, participant)["position"])
		if len(pos) == 0 {
			continue
		}
		sample.weights[presetZone(presetN(pos["x"]), presetN(pos["y"]))]++
		sample.total++
		sample.minutePositions = append(sample.minutePositions, object{"x": pos["x"], "y": pos["y"], "lane": []string{"top", "mid", "bot"}[presetZone(presetN(pos["x"]), presetN(pos["y"]))], "minute": i})
	}
	type camp struct {
		x, y       float64
		name, side string
	}
	camps := []camp{{3830, 7880, "blue", "blue"}, {3800, 6440, "wolves", "blue"}, {7760, 4010, "red", "blue"}, {6970, 5460, "raptors", "blue"}, {10990, 7000, "blue", "red"}, {11020, 8440, "wolves", "red"}, {7060, 10870, "red", "red"}, {7850, 9420, "raptors", "red"}}
	pos := presetObject(presetFramePlayer(frames, 1, participant)["position"])
	if len(pos) > 0 {
		dist := math.Inf(1)
		for _, c := range camps {
			d := math.Pow(presetN(pos["x"])-c.x, 2) + math.Pow(presetN(pos["y"])-c.y, 2)
			if d < dist {
				dist = d
				sample.camp = c.name
				sample.side = c.side
			}
		}
	}
	kill3, kill4 := false, false
	for _, entry := range frames {
		for _, rawEvent := range client.List(presetObject(entry)["events"]) {
			e := presetObject(rawEvent)
			timestamp := presetN(e["timestamp"])
			switch client.String(e["type"]) {
			case "CHAMPION_KILL":
				if !presetInvolved(e, participant) {
					continue
				}
				if timestamp <= 840000 {
					pos := presetObject(e["position"])
					sample.weights[presetZone(presetN(pos["x"]), presetN(pos["y"]))] += 5
					sample.total += 5
					if lane := presetGankLane(presetN(pos["x"]), presetN(pos["y"])); lane != "" {
						sample.gankPositions = append(sample.gankPositions, object{"x": pos["x"], "y": pos["y"], "lane": lane})
					}
				}
				pos := presetObject(e["position"])
				point := object{"x": pos["x"], "y": pos["y"], "lane": []string{"top", "mid", "bot"}[presetZone(presetN(pos["x"]), presetN(pos["y"]))]}
				if timestamp <= 180000 {
					kill3 = true
					sample.level3Positions = append(sample.level3Positions, point)
				} else if timestamp <= 240000 {
					kill4 = true
					sample.level4Positions = append(sample.level4Positions, point)
				}
			case "ELITE_MONSTER_KILL":
				team := int(presetN(e["killerTeamId"]))
				if team == 0 {
					team = 200
					id := presetN(e["killerId"])
					if id >= 1 && id <= 5 {
						team = 100
					}
				}
				ours := team == sample.team
				switch client.String(e["monsterType"]) {
				case "DRAGON":
					if sample.firstDragon == nil {
						first := ours
						sample.firstDragon = &first
					}
					if ours {
						sample.dragons++
						if int(presetN(e["killerId"])) == participant && len(client.List(e["assistingParticipantIds"])) == 0 {
							sample.soloDragons++
						}
						if sample.dragonTime == nil {
							seconds := timestamp / 1000
							sample.dragonTime = &seconds
						}
					}
				case "HORDE":
					if ours {
						sample.voidgrubs++
						if sample.grubTime == nil {
							seconds := timestamp / 1000
							sample.grubTime = &seconds
						}
					}
				case "RIFTHERALD":
					if ours {
						sample.heralds++
						if sample.heraldTime == nil {
							seconds := timestamp / 1000
							sample.heraldTime = &seconds
						}
					}
				case "BARON_NASHOR":
					if ours {
						sample.barons++
						if sample.baronTime == nil {
							seconds := timestamp / 1000
							sample.baronTime = &seconds
						}
					}
				}
			}
		}
	}
	p3 := presetFramePlayer(frames, 3, participant)
	if len(p3) > 0 {
		cs := presetN(p3["minionsKilled"]) + presetN(p3["jungleMinionsKilled"])
		damage3 := presetN(presetPath(p3, "damageStats", "totalDamageDoneToChampions"))
		has3 := kill3
		if p3["damageStats"] != nil && p3["championStats"] != nil {
			has3 = damage3 > 0
		}
		sample.level3 = cs >= 12 && cs < 20 && presetN(p3["level"]) == 3 && has3
		p4 := presetFramePlayer(frames, 4, participant)
		if len(p4) > 0 {
			sample.level4 = kill4
			if p4["damageStats"] != nil && p4["championStats"] != nil {
				sample.level4 = presetN(presetPath(p4, "damageStats", "totalDamageDoneToChampions")) > damage3 || kill4
			}
		}
	}
	return sample
}
func presetAggregateJungle(samples []*presetJungleSample) any {
	if len(samples) == 0 {
		return nil
	}
	fc := object{"blue": object{}, "red": object{}, "blueInvade": object{}, "redInvade": object{}, "blueGames": 0.0, "redGames": 0.0}
	for _, key := range []string{"blue", "red", "blueInvade", "redInvade"} {
		for _, camp := range []string{"blue", "red", "wolves", "raptors"} {
			presetObject(fc[key])[camp] = 0.0
		}
	}
	weights := [3]float64{}
	total, level3, level4, firstCount, firstWins, dragonTime, dragonTimes, dragons, grubs, heralds, barons := 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0
	for _, s := range samples {
		for i, w := range s.weights {
			weights[i] += w
		}
		total += s.total
		if s.level3 {
			level3++
		}
		if s.level4 {
			level4++
		}
		if s.camp != "" {
			side := "red"
			if s.team == 100 {
				side = "blue"
			}
			fc[side+"Games"] = presetN(fc[side+"Games"]) + 1
			key := side
			if s.side != side {
				key += "Invade"
			}
			record := presetObject(fc[key])
			record[s.camp] = presetN(record[s.camp]) + 1
		}
		if s.firstDragon != nil {
			firstCount++
			if *s.firstDragon {
				firstWins++
			}
		}
		if s.dragonTime != nil {
			dragonTime += *s.dragonTime
			dragonTimes++
		}
		dragons += s.dragons
		grubs += s.voidgrubs
		heralds += s.heralds
		barons += s.barons
	}
	count := float64(len(samples))
	var timeValue any
	if dragonTimes > 0 {
		timeValue = dragonTime / dragonTimes
	}
	share := func(n, d float64) float64 {
		if d == 0 {
			return 0
		}
		return n / d
	}
	minutes, ganks, positions3, positions4 := []any{}, []any{}, []any{}, []any{}
	byTeam := object{}
	gankCounts := map[string]float64{"top": 0, "mid": 0, "bot": 0}
	for _, s := range samples {
		minutes = append(minutes, s.minutePositions...)
		ganks = append(ganks, s.gankPositions...)
		positions3 = append(positions3, s.level3Positions...)
		positions4 = append(positions4, s.level4Positions...)
		side := "red"
		if s.team == 100 {
			side = "blue"
		}
		byTeam[side+"Games"] = presetN(byTeam[side+"Games"]) + 1
		for _, entry := range []struct {
			level     int
			active    bool
			positions []any
		}{{3, s.level3, s.level3Positions}, {4, s.level4, s.level4Positions}} {
			prefix := side + "Level" + strconv.Itoa(entry.level)
			if entry.active {
				byTeam[prefix+"GankCount"] = presetN(byTeam[prefix+"GankCount"]) + 1
			}
			byTeam[prefix+"KillPositions"] = append(client.List(byTeam[prefix+"KillPositions"]), entry.positions...)
		}
		for _, point := range s.gankPositions {
			gankCounts[client.String(presetObject(point)["lane"])]++
		}
	}
	for _, side := range []string{"blue", "red"} {
		for _, level := range []int{3, 4} {
			prefix := side + "Level" + strconv.Itoa(level)
			byTeam[prefix+"GankCount"] = presetN(byTeam[prefix+"GankCount"])
			byTeam[prefix+"GankRate"] = share(presetN(byTeam[prefix+"GankCount"]), presetN(byTeam[side+"Games"]))
			if byTeam[prefix+"KillPositions"] == nil {
				byTeam[prefix+"KillPositions"] = []any{}
			}
		}
		byTeam[side+"Games"] = presetN(byTeam[side+"Games"])
	}
	averageTime := func(get func(*presetJungleSample) *float64) any {
		sum, n := 0.0, 0.0
		for _, s := range samples {
			if tm := get(s); tm != nil {
				sum += *tm
				n++
			}
		}
		if n == 0 {
			return nil
		}
		return sum / n
	}
	solo := 0.0
	for _, s := range samples {
		solo += s.soloDragons
	}
	return object{"gamesAnalyzed": count, "topZoneWeightSum": weights[0], "midZoneWeightSum": weights[1], "botZoneWeightSum": weights[2], "totalZoneWeightSum": total, "avgTopZonePercentage": share(weights[0], total), "avgMidZonePercentage": share(weights[1], total), "avgBotZonePercentage": share(weights[2], total), "firstClearCamp": fc, "totalTopGanks": gankCounts["top"], "totalMidGanks": gankCounts["mid"], "totalBotGanks": gankCounts["bot"], "avgTopGanks": gankCounts["top"] / count, "avgMidGanks": gankCounts["mid"] / count, "avgBotGanks": gankCounts["bot"] / count, "earlyGank": object{"byTeam": byTeam, "level3GankCount": level3, "level4GankCount": level4, "level3GankRate": level3 / count, "level4GankRate": level4 / count, "level3KillPositions": positions3, "level4KillPositions": positions4}, "objectives": object{"firstDragonRate": share(firstWins, firstCount), "soloDragonRate": share(solo, dragons), "avgFirstDragonTime": timeValue, "avgFirstVoidgrubTime": averageTime(func(s *presetJungleSample) *float64 { return s.grubTime }), "avgFirstHeraldTime": averageTime(func(s *presetJungleSample) *float64 { return s.heraldTime }), "avgFirstBaronTime": averageTime(func(s *presetJungleSample) *float64 { return s.baronTime }), "avgDragons": dragons / count, "avgVoidgrubs": grubs / count, "avgHeralds": heralds / count, "avgBarons": barons / count}, "minutePositions": minutes, "gankPositions": ganks}
}
func presetGankLane(x, y float64) string {
	if x < 5000 && y > 9000 {
		return "top"
	}
	if x > 9000 && y < 5000 {
		return "bot"
	}
	if math.Abs(y-x) < 4000 && (x+y)/2 > 3000 && (x+y)/2 < 12000 {
		return "mid"
	}
	return ""
}
func presetSummaryRows(data object, puuid string) []any {
	var rows []any
	b, _ := json.Marshal(presetObject(presetObject(data["matchHistory"])[puuid])["data"])
	_ = json.Unmarshal(b, &rows)
	return rows
}
func presetSummaryParticipant(wrapped object, puuid string) (object, object) {
	g := presetObject(wrapped["data"])
	if client.String(wrapped["source"]) == "sgp" {
		g = presetObject(g["json"])
	}
	wanted := 0
	for _, raw := range client.List(g["participantIdentities"]) {
		i := presetObject(raw)
		if client.String(presetPath(i, "player", "puuid")) == puuid {
			wanted = int(presetN(i["participantId"]))
		}
	}
	for _, raw := range client.List(g["participants"]) {
		p := presetObject(raw)
		if client.String(p["puuid"]) == puuid || (wanted > 0 && int(presetN(p["participantId"])) == wanted) {
			return g, p
		}
	}
	return g, object{}
}
func presetIsJungle(g, p object) bool {
	return presetN(g["mapId"]) == 11 && (p["teamPosition"] == "JUNGLE" || presetN(p["spell1Id"]) == 11 || presetN(p["spell2Id"]) == 11 || presetN(p["summoner1Id"]) == 11 || presetN(p["summoner2Id"]) == 11)
}
func presetAttachJungle(a, data object, puuid string) {
	samples := []*presetJungleSample{}
	byChampion := map[int][]*presetJungleSample{}
	for _, row := range presetSummaryRows(data, puuid) {
		w := presetObject(row)
		g, p := presetSummaryParticipant(w, puuid)
		if !presetIsJungle(g, p) {
			continue
		}
		id := int(presetN(p["championId"]))
		sample := presetAnalyzeJungleTimeline(presetObject(data["gameDetails"])[strconv.FormatInt(int64(presetN(w["gameId"])), 10)], p, id)
		if sample != nil {
			samples = append(samples, sample)
			byChampion[id] = append(byChampion[id], sample)
		}
	}
	a["jungle"] = presetAggregateJungle(samples)
	for id, samples := range byChampion {
		ch := presetObject(presetObject(a["champions"])[strconv.Itoa(id)])
		if len(ch) > 0 {
			ch["jungle"] = presetAggregateJungle(samples)
		}
	}
}
func (d *Desktop) preparePresetJungle(ctx context.Context, c *sendPresetContext, opts object, selected any) error {
	details := presetObject(c.data["gameDetails"])
	c.data["gameDetails"] = details
	limit := int(presetN(d.settingValue("ongoing-game-main", "gameDetailsLoadCount")))
	for _, player := range presetSelectedPlayers(c.teams(), selected) {
		loaded := 0
		for _, row := range presetSummaryRows(c.data, player.puuid) {
			w := presetObject(row)
			g, p := presetSummaryParticipant(w, player.puuid)
			if !presetIsJungle(g, p) {
				continue
			}
			if opts["showCurrentChampion"] == true && int(presetN(p["championId"])) != player.champion {
				continue
			}
			if limit > 0 && loaded >= limit {
				break
			}
			id := int64(presetN(w["gameId"]))
			if id <= 0 {
				continue
			}
			key := strconv.FormatInt(id, 10)
			if details[key] == nil {
				value, err := d.game.GetTimeline(ctx, "", id)
				if err != nil {
					return fmt.Errorf("获取打野时间线 %d：%w", id, err)
				}
				details[key] = value
			}
			loaded++
		}
		a := presetObject(presetPath(c.state, "analysis", "players", player.puuid))
		if len(a) == 0 {
			a = presetAnalyzeHistory(c.data, player.puuid)
		}
		presetAttachJungle(a, c.data, player.puuid)
		analysis := presetObject(c.state["analysis"])
		players := presetObject(analysis["players"])
		players[player.puuid] = a
		analysis["players"] = players
		c.state["analysis"] = analysis
	}
	return nil
}
