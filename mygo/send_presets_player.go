package main

import (
	"context"
	"encoding/json"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"hash/fnv"
	"strconv"
	"sync"
)

type presetPlayerCacheKey struct {
	desktop   *Desktop
	puuid     string
	signature uint64
}

var presetPlayerCache = struct {
	sync.Mutex
	values map[presetPlayerCacheKey]object
}{values: map[presetPlayerCacheKey]object{}}

func presetEarlyDeaths(value any, g, p object) *float64 {
	if presetN(g["mapId"]) != 11 || g["gameMode"] != "CLASSIC" || g["gameType"] != "MATCHED_GAME" || presetIsJungle(g, p) {
		return nil
	}
	enemyIds := map[int]bool{}
	for _, v := range client.List(g["participants"]) {
		enemy := presetObject(v)
		if presetN(enemy["teamId"]) != presetN(p["teamId"]) && enemy["teamPosition"] == "JUNGLE" {
			enemyIds[int(presetN(enemy["participantId"]))] = true
		}
	}
	if len(enemyIds) == 0 {
		for _, v := range client.List(g["participants"]) {
			enemy := presetObject(v)
			if presetN(enemy["teamId"]) != presetN(p["teamId"]) && presetIsJungle(g, enemy) {
				enemyIds[int(presetN(enemy["participantId"]))] = true
			}
		}
	}
	if len(enemyIds) == 0 {
		return nil
	}
	frames := presetTimelineFrames(value)
	if len(frames) == 0 {
		return nil
	}
	count := 0.0
	for _, raw := range frames {
		for _, entry := range client.List(presetObject(raw)["events"]) {
			e := presetObject(entry)
			if e["type"] != "CHAMPION_KILL" || presetN(e["timestamp"]) > 900000 || presetN(e["victimId"]) != presetN(p["participantId"]) {
				continue
			}
			involved := enemyIds[int(presetN(e["killerId"]))]
			for _, id := range client.List(e["assistingParticipantIds"]) {
				involved = involved || enemyIds[int(presetN(id))]
			}
			if involved {
				count++
			}
		}
	}
	return &count
}
func (d *Desktop) generatePlayerAnalysis(ctx context.Context, puuid string, options object) (any, error) {
	data := presetObject(d.game.GetAll())
	history := presetObject(data["matchHistory"])[puuid]
	detailIDs := []string{}
	for _, row := range presetSummaryRows(data, puuid) {
		id := strconv.FormatInt(int64(presetN(presetObject(row)["gameId"])), 10)
		if presetObject(data["gameDetails"])[id] != nil {
			detailIDs = append(detailIDs, id)
		}
	}
	encoded, _ := json.Marshal(object{"history": history, "options": options, "detailsLimit": d.settingValue("ongoing-game-main", "gameDetailsLoadCount"), "cachedDetails": detailIDs})
	hasher := fnv.New64a()
	_, _ = hasher.Write(encoded)
	key := presetPlayerCacheKey{desktop: d, puuid: puuid, signature: hasher.Sum64()}
	presetPlayerCache.Lock()
	cached := presetPlayerCache.values[key]
	presetPlayerCache.Unlock()
	if cached != nil {
		return cached, nil
	}
	a := presetAnalyzeHistory(data, puuid)
	if len(a) == 0 {
		return nil, nil
	}
	includeJungle := options["includeJungle"] == true
	includeDetails := options["includeDetails"] == true
	details := presetObject(data["gameDetails"])
	data["gameDetails"] = details
	limit := int(presetN(d.settingValue("ongoing-game-main", "gameDetailsLoadCount")))
	loaded := 0
	earlySum, earlyCount := 0.0, 0.0
	for _, raw := range presetSummaryRows(data, puuid) {
		w := presetObject(raw)
		g, p := presetSummaryParticipant(w, puuid)
		jungle := presetIsJungle(g, p)
		if presetN(g["mapId"]) != 11 || (!includeJungle || !jungle) && (!includeDetails || jungle || g["gameMode"] != "CLASSIC") {
			continue
		}
		if limit > 0 && loaded >= limit {
			break
		}
		id := int64(presetN(w["gameId"]))
		if id <= 0 {
			continue
		}
		idKey := strconv.FormatInt(id, 10)
		if details[idKey] == nil {
			// Card refresh obeys the preload setting. Preset sending is an
			// explicit analysis action and has its own on-demand loader.
			if limit == 0 {
				continue
			}
			timeline, err := d.game.GetTimeline(ctx, "", id)
			if err != nil {
				return nil, err
			}
			details[idKey] = timeline
		}
		loaded++
		if includeDetails {
			if count := presetEarlyDeaths(details[idKey], g, p); count != nil {
				earlySum += *count
				earlyCount++
			}
		}
	}
	if includeJungle {
		presetAttachJungle(a, data, puuid)
	}
	if includeDetails && earlyCount > 0 {
		a["details"] = object{"avgEarlyDeathsWithEnemyJunglerInvolved": earlySum / earlyCount}
		a["detailsCount"] = earlyCount
	}
	presetPlayerCache.Lock()
	if len(presetPlayerCache.values) >= 64 {
		presetPlayerCache.values = map[presetPlayerCacheKey]object{}
	}
	presetPlayerCache.values[key] = a
	presetPlayerCache.Unlock()
	return a, nil
}
