package automation

import (
	"context"
	"encoding/json"
	"fmt"
	"math/rand/v2"
	"net/url"
	"slices"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type receivedInvitation struct {
	InvitationID        string `json:"invitationId"`
	State               string `json:"state"`
	CanAcceptInvitation bool   `json:"canAcceptInvitation"`
	GameConfig          struct {
		InviteGameType string `json:"inviteGameType"`
	} `json:"gameConfig"`
}

func (runner *Runner) handleInvitation(ctx context.Context, config GameflowSettings) (bool, error) {
	var invitations []receivedInvitation
	if err := runner.get(ctx, "/lol-lobby/v2/received-invitations", &invitations); err != nil {
		if missingResource(err) {
			return false, nil
		}
		return false, err
	}
	if len(invitations) == 0 {
		runner.lastInvitationState = ""
		return false, nil
	}
	availability := ""
	if config.RejectInvitationWhenAway {
		var me struct {
			Availability string `json:"availability"`
		}
		if err := runner.get(ctx, "/lol-chat/v1/me", &me); err != nil {
			return false, err
		}
		availability = me.Availability
	}
	signature := stateSignature(invitations, config.InvitationHandlingStrategies, config.RejectInvitationWhenAway, availability)
	if signature == runner.lastInvitationState {
		return false, nil
	}
	runner.lastInvitationState = signature
	// The original away option suppresses handling. It does not reject the invite.
	if config.RejectInvitationWhenAway && availability == "away" {
		return false, nil
	}
	var candidate *receivedInvitation
	strategy := "ignore"
	priority := 3
	for index := range invitations {
		invitation := &invitations[index]
		if invitation.State != "Pending" || !invitation.CanAcceptInvitation {
			continue
		}
		rule := invitationRule(config, invitation.GameConfig.InviteGameType)
		rank := invitationPriority(rule)
		if rank < priority {
			candidate, strategy, priority = invitation, rule, rank
		}
	}
	if candidate == nil || (strategy != "accept" && strategy != "decline") {
		return false, nil
	}
	// Recheck the selected invite after settings/state reads before submitting it.
	var fresh []receivedInvitation
	if err := runner.get(ctx, "/lol-lobby/v2/received-invitations", &fresh); err != nil {
		return false, err
	}
	valid := false
	for _, invitation := range fresh {
		if invitation.InvitationID == candidate.InvitationID && invitation.State == "Pending" && invitation.CanAcceptInvitation && invitationRule(config, invitation.GameConfig.InviteGameType) == strategy {
			valid = true
			break
		}
	}
	if !valid || runner.settings.Get(settings.GameflowNamespace, "autoHandleInvitationsEnabled") != true {
		return false, nil
	}
	if config.RejectInvitationWhenAway {
		var me struct {
			Availability string `json:"availability"`
		}
		if err := runner.get(ctx, "/lol-chat/v1/me", &me); err != nil {
			return false, err
		}
		if me.Availability == "away" {
			return false, nil
		}
	}
	path := "/lol-lobby/v2/received-invitations/" + url.PathEscape(candidate.InvitationID) + "/" + strategy
	return true, runner.write(ctx, settings.GameflowNamespace, "error-handle-invitation", "POST", path, nil)
}

func invitationRule(config GameflowSettings, gameType string) string {
	if rule := config.InvitationHandlingStrategies[gameType]; rule != "" {
		return rule
	}
	if rule := config.InvitationHandlingStrategies["<DEFAULT>"]; rule != "" {
		return rule
	}
	return "ignore"
}

func invitationPriority(rule string) int {
	switch rule {
	case "accept":
		return 0
	case "decline":
		return 1
	default:
		return 2
	}
}

type leaderLobby struct {
	LocalMember struct {
		IsLeader   bool  `json:"isLeader"`
		SummonerID int64 `json:"summonerId"`
	} `json:"localMember"`
	Members []struct {
		SummonerID  int64 `json:"summonerId"`
		IsSpectator bool  `json:"isSpectator"`
		Ready       bool  `json:"ready"`
	} `json:"members"`
}

func (runner *Runner) transferLeader(ctx context.Context, config GameflowSettings) (bool, error) {
	var lobby *leaderLobby
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &lobby); err != nil {
		if missingResource(err) {
			runner.lastLeaderState = ""
			return false, nil
		}
		return false, err
	}
	if lobby == nil {
		runner.lastLeaderState = ""
		return false, nil
	}
	var ready, notReady []int64
	for _, member := range lobby.Members {
		if member.SummonerID == lobby.LocalMember.SummonerID || member.IsSpectator || member.SummonerID <= 0 {
			continue
		}
		if member.Ready {
			ready = append(ready, member.SummonerID)
		} else {
			notReady = append(notReady, member.SummonerID)
		}
	}
	signature := stateSignature(lobby.LocalMember.IsLeader, ready, notReady)
	if signature == runner.lastLeaderState {
		return false, nil
	}
	runner.lastLeaderState = signature
	if !lobby.LocalMember.IsLeader {
		return false, nil
	}
	candidates := ready
	if len(candidates) == 0 {
		candidates = notReady
	}
	if len(candidates) == 0 {
		return false, nil
	}
	target := candidates[rand.IntN(len(candidates))]
	var fresh *leaderLobby
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &fresh); err != nil {
		return false, err
	}
	if fresh == nil || !fresh.LocalMember.IsLeader || runner.settings.Get(settings.GameflowNamespace, "autoSkipLeaderEnabled") != true {
		return false, nil
	}
	valid := false
	for _, member := range fresh.Members {
		if member.SummonerID == target && member.SummonerID != fresh.LocalMember.SummonerID && !member.IsSpectator {
			valid = true
			break
		}
	}
	if !valid {
		return false, nil
	}
	// Keep the existing custom-room message, without blocking leader transfer if chat is unavailable.
	_ = runner.customRoomMessage(ctx, "[LeagueAkari-MyGo] 尝试将房主转让")
	return true, runner.write(ctx, settings.GameflowNamespace, "error-transfer-leader", "POST", fmt.Sprintf("/lol-lobby/v2/lobby/members/%d/promote", target), nil)
}

func (runner *Runner) customRoomMessage(ctx context.Context, message string) error {
	var conversations []struct {
		ID   string `json:"id"`
		Type string `json:"type"`
	}
	if err := runner.get(ctx, "/lol-chat/v1/conversations", &conversations); err != nil {
		return err
	}
	for _, conversation := range conversations {
		if conversation.Type == "customGame" {
			return runner.write(ctx, settings.GameflowNamespace, "error-lobby-chat", "POST", "/lol-chat/v1/conversations/"+url.PathEscape(conversation.ID)+"/messages", map[string]any{"body": message, "fromPid": "", "fromSummonerId": 0, "id": conversation.ID, "isHistorical": false, "timestamp": "", "type": "celebration"})
		}
	}
	return nil
}

func stateSignature(values ...any) string { data, _ := json.Marshal(values); return string(data) }

// queueProgress preserves the initial penalty duration for the current queue attempt.
type queueProgress struct {
	active          bool
	key             string
	penalty         float64
	lastTime        float64
	cancelAttempted bool
}

type queueSearch struct {
	LobbyID            string  `json:"lobbyId"`
	QueueID            int     `json:"queueId"`
	SearchState        string  `json:"searchState"`
	IsCurrentlyInQueue bool    `json:"isCurrentlyInQueue"`
	TimeInQueue        float64 `json:"timeInQueue"`
	EstimatedQueueTime float64 `json:"estimatedQueueTime"`
	LowPriorityData    struct {
		PenaltyTime          float64 `json:"penaltyTime"`
		PenaltyTimeRemaining float64 `json:"penaltyTimeRemaining"`
	} `json:"lowPriorityData"`
}

func (runner *Runner) restartMatchmaking(ctx context.Context, config GameflowSettings) error {
	if config.AutoMatchmakingRematchStrategy == "never" || (config.AutoMatchmakingRematchStrategy != "fixed-duration" && config.AutoMatchmakingRematchStrategy != "estimated-duration") {
		return nil
	}
	var search *queueSearch
	if err := runner.get(ctx, "/lol-matchmaking/v1/search", &search); err != nil {
		if missingResource(err) {
			runner.queueProgress = queueProgress{}
			return nil
		}
		return err
	}
	if search == nil || search.SearchState != "Searching" || !search.IsCurrentlyInQueue {
		runner.queueProgress = queueProgress{}
		return nil
	}
	key := fmt.Sprintf("%s:%d", search.LobbyID, search.QueueID)
	progress := &runner.queueProgress
	if !progress.active || progress.key != key || search.TimeInQueue < progress.lastTime {
		*progress = queueProgress{active: true, key: key, penalty: search.LowPriorityData.PenaltyTime}
	}
	progress.lastTime = search.TimeInQueue
	if progress.cancelAttempted {
		return nil
	}
	limit := config.AutoMatchmakingRematchFixedDuration
	if config.AutoMatchmakingRematchStrategy == "estimated-duration" {
		limit = search.EstimatedQueueTime
	}
	if search.TimeInQueue-progress.penalty < limit {
		return nil
	}
	var fresh *queueSearch
	if err := runner.get(ctx, "/lol-matchmaking/v1/search", &fresh); err != nil {
		return err
	}
	if fresh == nil || fresh.SearchState != "Searching" || !fresh.IsCurrentlyInQueue || fresh.LobbyID != search.LobbyID || fresh.QueueID != search.QueueID || fresh.TimeInQueue < search.TimeInQueue {
		return nil
	}
	if !slices.Contains([]string{"fixed-duration", "estimated-duration"}, config.AutoMatchmakingRematchStrategy) {
		return nil
	}
	if runner.settings.Get(settings.GameflowNamespace, "autoMatchmakingRematchStrategy") != config.AutoMatchmakingRematchStrategy {
		return nil
	}
	var phase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		return err
	}
	if phase != "Matchmaking" {
		return nil
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	progress.cancelAttempted = true
	// The original rematch policy cancels even when auto-start is disabled. Re-entering
	// the queue remains controlled separately by autoMatchmakingEnabled after returning to Lobby.
	return runner.write(ctx, settings.GameflowNamespace, "error-matchmaking", "DELETE", "/lol-lobby/v2/lobby/matchmaking/search", nil)
}
