package main

import (
	"math"
	"reflect"
	"sort"
	"strings"
	"sync"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

type presetSelectionController struct {
	mu                sync.Mutex
	initialized       bool
	playersExpression any
	premadeExpression any
}

func presetCurrentPuuids(state object) []any {
	teams := asObject(state["teams"])
	keys := make([]string, 0, len(teams))
	for key := range teams {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	all := []any{}
	seen := map[string]bool{}
	for _, key := range keys {
		for _, value := range client.List(teams[key]) {
			id, ok := value.(string)
			if ok && !seen[id] {
				all = append(all, id)
				seen[id] = true
			}
		}
	}
	return all
}

func presetIndex(value any) (float64, bool) {
	var n float64
	switch value := value.(type) {
	case int:
		n = float64(value)
	case int64:
		n = float64(value)
	case float64:
		n = value
	default:
		return 0, false
	}
	return n, !math.IsNaN(n) && !math.IsInf(n, 0) && n == math.Trunc(n)
}

func filterPresetSelection(state object, key string, values any) []any {
	result := []any{}
	if key == "premadeIndices" {
		allowed, seen := map[float64]bool{}, map[float64]bool{}
		for _, value := range asObject(state["mergedPremadeTeamMap"]) {
			if n, ok := presetIndex(value); ok {
				allowed[n] = true
			}
		}
		for _, value := range client.List(values) {
			if n, ok := presetIndex(value); ok && allowed[n] && !seen[n] {
				result = append(result, n)
				seen[n] = true
			}
		}
		return result
	}
	allowed, seen := map[string]bool{}, map[string]bool{}
	for _, value := range presetCurrentPuuids(state) {
		allowed[value.(string)] = true
	}
	for _, value := range client.List(values) {
		if id, ok := value.(string); ok && allowed[id] && !seen[id] {
			result = append(result, id)
			seen[id] = true
		}
	}
	return result
}

func (d *Desktop) publishPresetSelection(key string, values []any) {
	const id = "in-game-send-main:state"
	d.mu.Lock()
	if d.static == nil {
		d.static = map[string]object{}
	}
	if d.static[id] == nil {
		d.static[id] = object{}
	}
	changed := !reflect.DeepEqual(d.static[id][key], values)
	d.static[id][key] = values
	d.mu.Unlock()
	if changed {
		d.update("in-game-send-main", "state", key, values)
	}
}

func (d *Desktop) syncPresetSelectionsLocked(state object) {
	controller := &d.presetSelections
	positions := asObject(state["positionAssignments"])
	spells := asObject(asObject(state["additional"])["spells"])
	expression := object{"teams": state["teams"], "positionAssignments": positions, "spells": spells}
	if !controller.initialized || !reflect.DeepEqual(controller.playersExpression, expression) {
		all, jungle := presetCurrentPuuids(state), []any{}
		for _, value := range all {
			id := value.(string)
			position := client.String(asObject(positions[id])["position"])
			spell := asObject(spells[id])
			if strings.EqualFold(position, "JUNGLE") || client.Number(spell["spell1Id"]) == 11 || client.Number(spell["spell2Id"]) == 11 {
				jungle = append(jungle, id)
			}
		}
		controller.playersExpression = client.Clone(expression)
		d.publishPresetSelection("ratingPuuids", all)
		d.publishPresetSelection("junglePuuids", jungle)
	}
	if !controller.initialized || !reflect.DeepEqual(controller.premadeExpression, state["mergedPremadeTeamMap"]) {
		indices := []any{}
		seen := map[float64]bool{}
		for _, value := range asObject(state["mergedPremadeTeamMap"]) {
			if n, ok := presetIndex(value); ok && !seen[n] {
				indices = append(indices, n)
				seen[n] = true
			}
		}
		sort.Slice(indices, func(i, j int) bool { return indices[i].(float64) < indices[j].(float64) })
		controller.premadeExpression = client.Clone(state["mergedPremadeTeamMap"])
		d.publishPresetSelection("premadeIndices", indices)
	}
	controller.initialized = true
}

func (d *Desktop) syncPresetSelections() {
	if d.game == nil {
		return
	}
	d.presetSelections.mu.Lock()
	defer d.presetSelections.mu.Unlock()
	d.syncPresetSelectionsLocked(d.game.State())
}

func (d *Desktop) setPresetSelection(key string, values any) {
	d.presetSelections.mu.Lock()
	defer d.presetSelections.mu.Unlock()
	state := d.game.State()
	d.syncPresetSelectionsLocked(state)
	d.publishPresetSelection(key, filterPresetSelection(state, key, values))
}
