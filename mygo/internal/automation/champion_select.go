package automation

import (
	"context"
	"encoding/json"
	"fmt"
	"slices"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type SelectGroup struct {
	GroupID         string `json:"groupId"`
	IsCustom        bool   `json:"isCustom"`
	TargetGameModes []struct {
		GameMode   string   `json:"gameMode"`
		QueueTypes []string `json:"queueTypes"`
	} `json:"targetGameModes"`
	Positions       []string `json:"positions"`
	AdditionalPicks []int    `json:"additionalPicks"`
	AdditionalBans  []int    `json:"additionalBans"`
	ExcludedPicks   []int    `json:"excludedPicks"`
	ExcludedBans    []int    `json:"excludedBans"`
}

type ChampionConfig struct {
	Enabled                           bool             `json:"enabled"`
	Champions                         map[string][]int `json:"champions"`
	DelaySeconds                      float64          `json:"delaySeconds"`
	Strategy                          string           `json:"strategy"`
	IgnoreIntent                      bool             `json:"ignoreIntent"`
	ShowIntent                        bool             `json:"showIntent"`
	BenchSelectFirstAvailableChampion bool             `json:"benchSelectFirstAvailableChampion"`
	BenchSwapAccumulatedDelaySeconds  float64          `json:"benchSwapAccumulatedDelaySeconds"`
	BenchHandleTradeEnabled           bool             `json:"benchHandleTradeEnabled"`
}

func DefaultChampionConfig() ChampionConfig {
	return ChampionConfig{
		Champions: map[string][]int{"default": {}, "top": {}, "jungle": {}, "middle": {}, "bottom": {}, "utility": {}},
		Strategy:  "show-and-lock-in", BenchSwapAccumulatedDelaySeconds: 2.9,
	}
}

type selectAction struct {
	ID           int    `json:"id"`
	ActorCellID  int    `json:"actorCellId"`
	ChampionID   int    `json:"championId"`
	Completed    bool   `json:"completed"`
	IsInProgress bool   `json:"isInProgress"`
	Type         string `json:"type"`
}

type selectMember struct {
	CellID           int    `json:"cellId"`
	ChampionID       int    `json:"championId"`
	AssignedPosition string `json:"assignedPosition"`
	Team             int    `json:"team"`
}

type selectSession struct {
	ID                       string           `json:"id"`
	GameID                   int64            `json:"gameId"`
	LocalPlayerCellID        int              `json:"localPlayerCellId"`
	MyTeam                   []selectMember   `json:"myTeam"`
	Actions                  [][]selectAction `json:"actions"`
	BenchEnabled             bool             `json:"benchEnabled"`
	AllowSubsetChampionPicks bool             `json:"allowSubsetChampionPicks"`
	AllowDuplicatePicks      bool             `json:"allowDuplicatePicks"`
	IsCustomGame             bool             `json:"isCustomGame"`
	IsSpectating             bool             `json:"isSpectating"`
	BenchChampions           []struct {
		ChampionID int `json:"championId"`
	} `json:"benchChampions"`
	Timer struct {
		Phase                   string  `json:"phase"`
		AdjustedTimeLeftInPhase float64 `json:"adjustedTimeLeftInPhase"`
		InternalNowInEpochMs    float64 `json:"internalNowInEpochMs"`
		IsInfinite              bool    `json:"isInfinite"`
	} `json:"timer"`
}

type gameSession struct {
	Map struct {
		GameMode string `json:"gameMode"`
	} `json:"map"`
	GameData struct {
		IsCustomGame bool `json:"isCustomGame"`
		Queue        struct {
			GameMode string `json:"gameMode"`
			Type     string `json:"type"`
		} `json:"queue"`
	} `json:"gameData"`
}

type gridChampion struct {
	ID              int `json:"id"`
	SelectionStatus struct {
		IsBanned              bool `json:"isBanned"`
		PickIntented          bool `json:"pickIntented"`
		PickIntentedByMe      bool `json:"pickIntentedByMe"`
		PickedByOtherOrBanned bool `json:"pickedByOtherOrBanned"`
		SelectedByMe          bool `json:"selectedByMe"`
	} `json:"selectionStatus"`
}

func (runner *Runner) selectChampion(ctx context.Context, epoch uint64) error {
	runner.flushLocalMessages(ctx)
	var session selectSession
	if err := runner.get(ctx, "/lol-champ-select/v1/session", &session); err != nil {
		return err
	}
	if session.IsSpectating || session.LocalPlayerCellID < 0 {
		return nil
	}
	var game gameSession
	if err := runner.get(ctx, "/lol-gameflow/v1/session", &game); err != nil {
		return err
	}
	group := runner.activeGroup(game, session.IsCustomGame)
	runner.mu.Lock()
	runner.state[settings.SelectNamespace]["activeGroupConfigId"] = group
	runner.mu.Unlock()
	if group == "" {
		return nil
	}
	pick, err := runner.championConfig("pickConfig", group)
	if err != nil {
		return err
	}
	ban, err := runner.championConfig("banConfig", group)
	if err != nil {
		return err
	}
	if !pick.Enabled && !ban.Enabled && !pick.BenchHandleTradeEnabled {
		runner.discardScheduled("pick")
		runner.discardScheduled("ban")
		runner.discardScheduled("bench")
		runner.discardScheduled("vote")
		runner.discardScheduled("trade")
		return nil
	}
	var member *selectMember
	for index := range session.MyTeam {
		if session.MyTeam[index].CellID == session.LocalPlayerCellID {
			member = &session.MyTeam[index]
			break
		}
	}
	if member == nil {
		return nil
	}
	position := member.AssignedPosition
	if position == "" || !runner.groupSupportsPosition(group, position) {
		position = "default"
	}
	if pick.BenchHandleTradeEnabled && session.BenchEnabled {
		handled, err := runner.championTrade(ctx, epoch, group, session, *member, pick, position)
		if err != nil {
			return err
		}
		if handled {
			return nil
		}
	}
	for _, actions := range session.Actions {
		for _, action := range actions {
			if action.ActorCellID != session.LocalPlayerCellID || action.Completed {
				continue
			}
			if action.IsInProgress && (action.Type == "pick" || action.Type == "ban" || action.Type == "vote") {
				config := pick
				if action.Type == "ban" {
					config = ban
				}
				return runner.pickOrBan(ctx, epoch, group, session, action, config, position, false)
			}
			if action.Type == "pick" && pick.Enabled && pick.ShowIntent && session.Timer.Phase == "PLANNING" {
				return runner.pickOrBan(ctx, epoch, group, session, action, pick, position, true)
			}
		}
	}
	if session.BenchEnabled && pick.Enabled && member.ChampionID > 0 {
		return runner.benchSwap(ctx, epoch, group, session, *member, pick, position)
	}
	for _, operation := range []string{"pick", "vote", "ban", "bench", "trade"} {
		runner.discardScheduled(operation)
	}
	return nil
}

func (runner *Runner) groupSupportsPosition(groupID, position string) bool {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	for _, group := range runner.groups {
		if group.GroupID == groupID {
			return slices.Contains(group.Positions, position)
		}
	}
	return true
}

func (runner *Runner) activeGroup(game gameSession, isCustom bool) string {
	runner.mu.Lock()
	groups := append([]SelectGroup(nil), runner.groups...)
	runner.mu.Unlock()
	mode, queue := game.GameData.Queue.GameMode, game.GameData.Queue.Type
	for _, group := range groups {
		if group.IsCustom != (isCustom || game.GameData.IsCustomGame) {
			continue
		}
		for _, target := range group.TargetGameModes {
			if target.GameMode == mode && (slices.Contains(target.QueueTypes, queue) || slices.Contains(target.QueueTypes, "*")) {
				return group.GroupID
			}
		}
	}
	if len(groups) == 0 && !isCustom && !game.GameData.IsCustomGame {
		for _, key := range []string{queue, mode} {
			for _, settingKey := range []string{"pickConfig", "banConfig"} {
				values, _ := runner.settings.Get(settings.SelectNamespace, settingKey).(map[string]any)
				if key != "" && values[key] != nil {
					return key
				}
			}
		}
	}
	return ""
}

func (runner *Runner) championConfig(key, group string) (ChampionConfig, error) {
	config := DefaultChampionConfig()
	values, _ := runner.settings.Get(settings.SelectNamespace, key).(map[string]any)
	if values[group] == nil {
		return config, nil
	}
	data, err := json.Marshal(values[group])
	if err != nil {
		return config, err
	}
	err = json.Unmarshal(data, &config)
	return config, err
}

func (runner *Runner) pickOrBan(ctx context.Context, epoch uint64, group string, session selectSession, action selectAction, config ChampionConfig, position string, intent bool) error {
	if !config.Enabled || (config.Strategy != "just-show" && config.Strategy != "show-and-lock-in" && config.Strategy != "lock-in-immediately") {
		runner.discardScheduled(action.Type)
		return nil
	}
	if action.ChampionID != 0 && (intent || config.Strategy == "just-show") {
		runner.discardScheduled(action.Type)
		return nil
	}
	champions := config.Champions[position]
	var game gameSession
	if action.Type != "ban" {
		if err := runner.get(ctx, "/lol-gameflow/v1/session", &game); err != nil {
			return err
		}
	}
	var available []int
	path := "/lol-champ-select/v1/pickable-champion-ids"
	if action.Type == "ban" {
		path = "/lol-champ-select/v1/bannable-champion-ids"
	}
	if err := runner.get(ctx, path, &available); err != nil {
		return err
	}
	if (action.Type == "pick" || action.Type == "vote") && session.AllowSubsetChampionPicks {
		var subset []int
		if err := runner.get(ctx, "/lol-lobby-team-builder/champ-select/v1/subset-champion-list", &subset); err != nil {
			return err
		}
		available = intersection(available, subset)
	}
	var grid []gridChampion
	if action.Type == "pick" || action.Type == "vote" {
		if err := runner.get(ctx, "/lol-champ-select/v1/all-grid-champions", &grid); err != nil {
			return err
		}
	}
	chosen := 0
	for _, champion := range champions {
		if champion == -3 && game.GameData.Queue.GameMode == "CHERRY" {
			chosen = -3
			break
		}
		if action.Type == "ban" && champion == -1 {
			chosen = -1
			break
		}
		if champion <= 0 || !slices.Contains(available, champion) {
			continue
		}
		if (action.Type == "pick" || action.Type == "vote") && !pickableGrid(champion, grid, config.IgnoreIntent, session.AllowDuplicatePicks) {
			continue
		}
		chosen = champion
		break
	}
	if chosen == 0 {
		runner.discardScheduled(action.Type)
		return nil
	}
	completed := !intent && (session.AllowSubsetChampionPicks || config.Strategy == "lock-in-immediately" || (config.Strategy == "show-and-lock-in" && action.ChampionID != 0))
	key := fmt.Sprintf("%d:%s:%d:%d:%t:%t", epoch, group, action.ID, chosen, completed, intent)
	delay := seconds(config.DelaySeconds)
	if !session.Timer.IsInfinite && session.Timer.InternalNowInEpochMs > 0 {
		remaining := session.Timer.AdjustedTimeLeftInPhase - float64(runner.now().UnixMilli()) + session.Timer.InternalNowInEpochMs
		delay = min(delay, time.Duration(max(remaining, 0))*time.Millisecond)
	}
	if !runner.ready(action.Type, key, delay) {
		runner.publishSelectDelay(action.Type, chosen, completed, intent, delay)
		runner.localMessage(ctx, key, fmt.Sprintf("将在 %.1f 秒后%s英雄 (%d)", delay.Seconds(), actionDescription(action.Type, completed, intent), chosen))
		return nil
	}
	runner.localMessage(ctx, key, fmt.Sprintf("%s英雄 (%d)", actionDescription(action.Type, completed, intent), chosen))
	var fresh selectSession
	if err := runner.get(ctx, "/lol-champ-select/v1/session", &fresh); err != nil {
		return err
	}
	if !sameSession(session, fresh) || !hasAction(fresh, action, intent) {
		runner.discardScheduled(action.Type)
		return nil
	}
	if err := runner.canSelect(ctx, group, action.Type); err != nil {
		return err
	}
	// Availability can change during the delay, especially when a teammate locks a pick.
	if chosen > 0 {
		var currentAvailable []int
		if err := runner.get(ctx, path, &currentAvailable); err != nil {
			return err
		}
		if !slices.Contains(currentAvailable, chosen) {
			runner.discardScheduled(action.Type)
			return nil
		}
		if action.Type == "pick" || action.Type == "vote" {
			var currentGrid []gridChampion
			if err := runner.get(ctx, "/lol-champ-select/v1/all-grid-champions", &currentGrid); err != nil {
				return err
			}
			if !pickableGrid(chosen, currentGrid, config.IgnoreIntent, fresh.AllowDuplicatePicks) {
				runner.discardScheduled(action.Type)
				return nil
			}
			if fresh.AllowSubsetChampionPicks {
				var subset []int
				if err := runner.get(ctx, "/lol-lobby-team-builder/champ-select/v1/subset-champion-list", &subset); err != nil {
					return err
				}
				if !slices.Contains(subset, chosen) {
					runner.discardScheduled(action.Type)
					return nil
				}
			}
		}
	}
	body := map[string]any{"championId": chosen}
	if !intent {
		body["type"] = action.Type
		body["completed"] = completed
	}
	runner.markAttempted(action.Type)
	return runner.write(ctx, settings.SelectNamespace, "error-"+action.Type, "PATCH", fmt.Sprintf("/lol-champ-select/v1/session/actions/%d", action.ID), body)
}

func (runner *Runner) benchSwap(ctx context.Context, epoch uint64, group string, session selectSession, member selectMember, config ChampionConfig, position string) error {
	champions := config.Champions[position]
	currentIndex := slices.Index(champions, member.ChampionID)
	if currentIndex >= 0 && !config.BenchSelectFirstAvailableChampion {
		runner.discardScheduled("bench")
		return nil
	}
	var available []int
	if err := runner.get(ctx, "/lol-champ-select/v1/pickable-champion-ids", &available); err != nil {
		return err
	}
	var bench []int
	for _, champion := range session.BenchChampions {
		bench = append(bench, champion.ChampionID)
	}
	if session.AllowSubsetChampionPicks && session.Timer.Phase == "BAN_PICK" {
		var subset []int
		if err := runner.get(ctx, "/lol-lobby-team-builder/champ-select/v1/subset-champion-list", &subset); err != nil {
			return err
		}
		bench = subset
	}
	chosen := 0
	for index, champion := range champions {
		if currentIndex >= 0 && index >= currentIndex {
			break
		}
		if champion > 0 && slices.Contains(bench, champion) && slices.Contains(available, champion) {
			chosen = champion
			break
		}
	}
	if chosen == 0 || chosen == member.ChampionID {
		runner.discardScheduled("bench")
		return nil
	}
	key := fmt.Sprintf("%d:%s:%d", epoch, group, chosen)
	delay := seconds(config.BenchSwapAccumulatedDelaySeconds)
	if !runner.ready("bench", key, delay) {
		runner.publishSelectDelay("bench", chosen, false, false, delay)
		runner.localMessage(ctx, key, fmt.Sprintf("即将在 %.1f 秒后交换 (%d)", delay.Seconds(), chosen))
		return nil
	}
	runner.localMessage(ctx, key, fmt.Sprintf("交换英雄 (%d)", chosen))
	var fresh selectSession
	if err := runner.get(ctx, "/lol-champ-select/v1/session", &fresh); err != nil {
		return err
	}
	if !sameSession(session, fresh) || !fresh.BenchEnabled {
		runner.discardScheduled("bench")
		return nil
	}
	if fresh.AllowSubsetChampionPicks && fresh.Timer.Phase == "BAN_PICK" {
		var subset []int
		if err := runner.get(ctx, "/lol-lobby-team-builder/champ-select/v1/subset-champion-list", &subset); err != nil {
			return err
		}
		if !slices.Contains(subset, chosen) {
			runner.discardScheduled("bench")
			return nil
		}
	} else {
		found := false
		for _, champion := range fresh.BenchChampions {
			if champion.ChampionID == chosen {
				found = true
			}
		}
		if !found {
			runner.discardScheduled("bench")
			return nil
		}
	}
	for _, current := range fresh.MyTeam {
		if current.CellID == fresh.LocalPlayerCellID && current.ChampionID == chosen {
			runner.markAttempted("bench")
			return nil
		}
	}
	if err := runner.canSelect(ctx, group, "pick"); err != nil {
		return err
	}
	runner.markAttempted("bench")
	return runner.write(ctx, settings.SelectNamespace, "error-bench-swap", "POST", fmt.Sprintf("/lol-champ-select/v1/session/bench/swap/%d", chosen), nil)
}

func (runner *Runner) canSelect(ctx context.Context, group, operation string) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	runner.mu.Lock()
	disabled := runner.temporarilyDisabled
	runner.mu.Unlock()
	if disabled {
		return context.Canceled
	}
	key := "pickConfig"
	if operation == "ban" {
		key = "banConfig"
	}
	config, err := runner.championConfig(key, group)
	if err != nil {
		return err
	}
	if !config.Enabled {
		return context.Canceled
	}
	var phase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		return err
	}
	if phase != "ChampSelect" {
		return context.Canceled
	}
	return nil
}

func (runner *Runner) publishSelectDelay(operation string, champion int, completed, intent bool, delay time.Duration) {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	if runner.cancelled[operation] {
		return
	}
	task := runner.scheduled[operation]
	if task.attempted {
		return
	}
	field := map[string]string{"pick": "delayedPick", "vote": "delayedPick", "ban": "delayedBan", "bench": "delayedBenchSwap"}[operation]
	runner.state[settings.SelectNamespace][field] = map[string]any{"championId": champion, "completed": completed, "isPickIntent": intent, "delayMs": delay.Milliseconds(), "startAt": task.due.Add(-delay).UnixMilli(), "finishAt": task.due.UnixMilli()}
}

func sameSession(a, b selectSession) bool {
	return a.ID == b.ID && a.GameID == b.GameID && a.LocalPlayerCellID == b.LocalPlayerCellID
}

func hasAction(session selectSession, expected selectAction, intent bool) bool {
	for _, actions := range session.Actions {
		for _, action := range actions {
			if action.ID == expected.ID && action.ActorCellID == session.LocalPlayerCellID && !action.Completed && action.Type == expected.Type {
				if intent {
					return session.Timer.Phase == "PLANNING" && action.ChampionID == 0
				}
				return action.IsInProgress && action.ChampionID == expected.ChampionID
			}
		}
	}
	return false
}

func pickableGrid(champion int, grid []gridChampion, ignoreIntent, duplicate bool) bool {
	for _, item := range grid {
		if item.ID == champion {
			status := item.SelectionStatus
			return !status.IsBanned && (duplicate || ((!status.PickedByOtherOrBanned || status.SelectedByMe) && (ignoreIntent || !status.PickIntented || status.PickIntentedByMe)))
		}
	}
	return false
}

func intersection(left, right []int) []int {
	var result []int
	for _, value := range left {
		if slices.Contains(right, value) {
			result = append(result, value)
		}
	}
	return result
}
