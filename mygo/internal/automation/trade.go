package automation

import (
	"context"
	"fmt"
	"slices"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type championSwap struct {
	ID                  int    `json:"id"`
	State               string `json:"state"`
	RequesterChampionID int    `json:"requesterChampionId"`
}

func (runner *Runner) championTrade(ctx context.Context, epoch uint64, group string, session selectSession, member selectMember, config ChampionConfig, position string) (bool, error) {
	var swap *championSwap
	if err := runner.get(ctx, "/lol-champ-select/v1/ongoing-champion-swap", &swap); err != nil {
		if missingResource(err) {
			runner.discardScheduled("trade")
			return false, nil
		}
		return false, err
	}
	if swap == nil || swap.State != "RECEIVED" {
		runner.discardScheduled("trade")
		return false, nil
	}
	action := "decline"
	champions := config.Champions[position]
	requestedIndex, currentIndex := slices.Index(champions, swap.RequesterChampionID), slices.Index(champions, member.ChampionID)
	if requestedIndex >= 0 && (currentIndex < 0 || (config.BenchSelectFirstAvailableChampion && requestedIndex < currentIndex)) {
		action = "accept"
	}
	key := fmt.Sprintf("%d:%s:%d:%d:%s", epoch, group, swap.ID, swap.RequesterChampionID, action)
	delay := seconds(config.DelaySeconds)
	if !session.Timer.IsInfinite && session.Timer.InternalNowInEpochMs > 0 {
		remaining := session.Timer.AdjustedTimeLeftInPhase - float64(runner.now().UnixMilli()) + session.Timer.InternalNowInEpochMs
		delay = min(delay, time.Duration(max(remaining, 0))*time.Millisecond)
	}
	if !runner.ready("trade", key, delay) {
		runner.publishTradeDelay(*swap, action, delay)
		runner.localMessage(ctx, key, fmt.Sprintf("将在 %.1f 秒后%s英雄交换请求 (%d)", delay.Seconds(), tradeDescription(action), swap.RequesterChampionID))
		return true, nil
	}
	var fresh selectSession
	if err := runner.get(ctx, "/lol-champ-select/v1/session", &fresh); err != nil {
		return true, err
	}
	if !sameSession(session, fresh) || !fresh.BenchEnabled {
		runner.discardScheduled("trade")
		return true, nil
	}
	var current *championSwap
	if err := runner.get(ctx, "/lol-champ-select/v1/ongoing-champion-swap", &current); err != nil {
		return true, err
	}
	if current == nil || current.ID != swap.ID || current.State != "RECEIVED" || current.RequesterChampionID != swap.RequesterChampionID {
		runner.discardScheduled("trade")
		return true, nil
	}
	currentConfig, err := runner.championConfig("pickConfig", group)
	if err != nil {
		return true, err
	}
	runner.mu.Lock()
	disabled := runner.temporarilyDisabled
	runner.mu.Unlock()
	if disabled || !currentConfig.BenchHandleTradeEnabled {
		return true, context.Canceled
	}
	for _, freshMember := range fresh.MyTeam {
		if freshMember.CellID == fresh.LocalPlayerCellID && freshMember.ChampionID != member.ChampionID {
			runner.discardScheduled("trade")
			return true, nil
		}
	}
	var phase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		return true, err
	}
	if phase != "ChampSelect" {
		return true, context.Canceled
	}
	runner.markAttempted("trade")
	runner.localMessage(ctx, key, fmt.Sprintf("%s英雄交换请求 (%d)", tradeDescription(action), swap.RequesterChampionID))
	return true, runner.write(ctx, settings.SelectNamespace, "error-champion-swap", "POST", fmt.Sprintf("/lol-champ-select/v1/session/champion-swaps/%d/%s", swap.ID, action), nil)
}

func (runner *Runner) publishTradeDelay(swap championSwap, action string, delay time.Duration) {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	task := runner.scheduled["trade"]
	if task.attempted {
		return
	}
	runner.state[settings.SelectNamespace]["delayedChampionSwap"] = map[string]any{
		"action": action, "tradeId": swap.ID, "requesterChampionId": swap.RequesterChampionID,
		"delayMs": delay.Milliseconds(), "startAt": task.due.Add(-delay).UnixMilli(), "finishAt": task.due.UnixMilli(),
	}
}

func tradeDescription(action string) string {
	if action == "accept" {
		return "接受"
	}
	return "拒绝"
}

func actionDescription(action string, completed, intent bool) string {
	if intent {
		return "预选"
	}
	if action == "vote" {
		return "投票选择"
	}
	if action == "ban" {
		return "禁用"
	}
	if completed {
		return "锁定"
	}
	return "选择"
}
