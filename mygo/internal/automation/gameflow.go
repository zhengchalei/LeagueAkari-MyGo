package automation

import (
	"context"
	"fmt"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func (runner *Runner) accept(ctx context.Context, phase string, epoch uint64, config GameflowSettings) error {
	if !config.AutoAcceptEnabled {
		return nil
	}
	var readyCheck struct {
		PlayerResponse string `json:"playerResponse"`
		State          string `json:"state"`
	}
	if err := runner.get(ctx, "/lol-matchmaking/v1/ready-check", &readyCheck); err != nil {
		return err
	}
	if readyCheck.PlayerResponse == "Accepted" || readyCheck.PlayerResponse == "Declined" || readyCheck.State == "Invalid" {
		runner.mu.Lock()
		delete(runner.scheduled, "accept")
		runner.clearOperationLocked("accept")
		runner.mu.Unlock()
		return nil
	}
	return runner.phaseAction(ctx, "accept", phase, epoch, seconds(config.AutoAcceptDelaySeconds), "/lol-matchmaking/v1/ready-check/accept", "autoAcceptEnabled")
}

func (runner *Runner) endOfGame(ctx context.Context, phase string, epoch uint64, config GameflowSettings) error {
	if !config.AutoHonorEnabled && !config.PlayAgainEnabled {
		return nil
	}
	if phase == "PreEndOfGame" && config.PlayAgainEnabled {
		var sequence any
		if err := runner.get(ctx, "/lol-pre-end-of-game/v1/currentSequenceEvent", &sequence); err == nil {
			if err := runner.completeMissionCelebration(ctx, sequence); err != nil {
				return err
			}
		}
	}
	var ballot *HonorBallot
	err := runner.get(ctx, "/lol-honor-v2/v1/ballot/", &ballot)
	if err != nil && !missingResource(err) {
		return err
	}
	if ballot != nil && ballot.GameID != 0 {
		if !config.AutoHonorEnabled {
			return nil
		}
		if err := runner.honor(ctx, ballot, config.AutoHonorStrategy); err != nil {
			return err
		}
		if !runner.honorGames[ballot.GameID].completed {
			return nil
		}
	}
	if !config.PlayAgainEnabled {
		return nil
	}
	delay := 1575 * time.Millisecond
	if phase == "WaitingForStats" {
		delay = 10 * time.Second
	}
	if phase == "PreEndOfGame" {
		delay = 3250 * time.Millisecond
	}
	return runner.phaseAction(ctx, "play-again", phase, epoch, delay, "/lol-lobby/v2/play-again", "playAgainEnabled")
}

func (runner *Runner) completeMissionCelebration(ctx context.Context, data any) error {
	var sequence struct {
		Name string `json:"name"`
	}
	if err := decodeJSON(data, &sequence); err != nil {
		return err
	}
	if sequence.Name != "missions-celebration" || runner.missionSkipAttempted || runner.settings.Get(settings.GameflowNamespace, "playAgainEnabled") != true {
		return nil
	}
	var phase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		return err
	}
	if phase != "PreEndOfGame" {
		return nil
	}
	if runner.settings.Get(settings.GameflowNamespace, "playAgainEnabled") != true {
		return context.Canceled
	}
	runner.missionSkipAttempted = true
	return runner.write(ctx, settings.GameflowNamespace, "error-skip-missions-celebration", "POST", "/lol-pre-end-of-game/v1/complete/missions-celebration", nil)
}

type matchmakingLobby struct {
	CanStartActivity bool   `json:"canStartActivity"`
	PartyID          string `json:"partyId"`
	LocalMember      struct {
		IsLeader             bool `json:"isLeader"`
		AllowedStartActivity bool `json:"allowedStartActivity"`
	} `json:"localMember"`
	Members []struct {
		IsBot       bool `json:"isBot"`
		IsSpectator bool `json:"isSpectator"`
	} `json:"members"`
	Invitations []struct {
		State string `json:"state"`
	} `json:"invitations"`
	GameConfig struct {
		IsCustom bool `json:"isCustom"`
	} `json:"gameConfig"`
}

func (runner *Runner) matchmake(ctx context.Context, phase string, epoch uint64, config GameflowSettings) error {
	if !config.AutoMatchmakingEnabled {
		return nil
	}
	var lobby matchmakingLobby
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &lobby); err != nil {
		return err
	}
	status := matchmakingStatus(lobby, config)
	runner.mu.Lock()
	runner.state[settings.GameflowNamespace]["activityStartStatus"] = status
	runner.mu.Unlock()
	if status != "can-start-activity" {
		runner.mu.Lock()
		delete(runner.scheduled, "matchmake")
		runner.clearOperationLocked("matchmake")
		runner.mu.Unlock()
		return nil
	}
	var search struct {
		IsCurrentlyInQueue bool   `json:"isCurrentlyInQueue"`
		SearchState        string `json:"searchState"`
		LowPriorityData    struct {
			PenaltyTimeRemaining float64 `json:"penaltyTimeRemaining"`
		} `json:"lowPriorityData"`
		Errors []struct {
			PenaltyTimeRemaining float64 `json:"penaltyTimeRemaining"`
		} `json:"errors"`
	}
	if err := runner.get(ctx, "/lol-matchmaking/v1/search", &search); err != nil && !missingResource(err) {
		return err
	}
	if search.IsCurrentlyInQueue || search.SearchState == "Searching" || search.LowPriorityData.PenaltyTimeRemaining > 0 {
		return nil
	}
	for _, penalty := range search.Errors {
		if penalty.PenaltyTimeRemaining > 0 {
			return nil
		}
	}
	key := fmt.Sprintf("%d:%s", epoch, lobby.PartyID)
	if !runner.ready("matchmake", key, seconds(config.AutoMatchmakingDelaySeconds)) {
		return nil
	}
	// Recheck the roster and invitations after the countdown, before starting a queue.
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &lobby); err != nil {
		return err
	}
	if matchmakingStatus(lobby, config) != "can-start-activity" {
		return nil
	}
	if runner.settings.Get(settings.GameflowNamespace, "autoMatchmakingEnabled") != true {
		return nil
	}
	var currentPhase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &currentPhase); err != nil {
		return err
	}
	if currentPhase != phase {
		return nil
	}
	runner.markAttempted("matchmake")
	return runner.write(ctx, settings.GameflowNamespace, "error-matchmaking", "POST", "/lol-lobby/v2/lobby/matchmaking/search", nil)
}

func matchmakingStatus(lobby matchmakingLobby, config GameflowSettings) string {
	if lobby.GameConfig.IsCustom || !lobby.CanStartActivity {
		return "cannot-start-activity"
	}
	if !lobby.LocalMember.IsLeader || !lobby.LocalMember.AllowedStartActivity {
		return "not-the-leader"
	}
	members := 0
	for _, member := range lobby.Members {
		if !member.IsBot && !member.IsSpectator {
			members++
		}
	}
	if members < max(config.AutoMatchmakingMinimumMembers, 1) {
		return "insufficient-members"
	}
	if config.AutoMatchmakingWaitForInvitees {
		for _, invitation := range lobby.Invitations {
			if invitation.State == "Pending" {
				return "waiting-for-invitees"
			}
		}
	}
	return "can-start-activity"
}

func seconds(value float64) time.Duration { return time.Duration(max(value, 0) * float64(time.Second)) }
