package automation

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"slices"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type friend struct {
	Puuid        string `json:"puuid"`
	SummonerID   int64  `json:"summonerId"`
	Availability string `json:"availability"`
	GameName     string `json:"gameName"`
	GameTag      string `json:"gameTag"`
}
type invitationLobby struct {
	LocalMember struct {
		AllowedInviteOthers bool `json:"allowedInviteOthers"`
	} `json:"localMember"`
	Members []struct {
		Puuid string `json:"puuid"`
	} `json:"members"`
}

func (runner *Runner) SetFriendsToBeInvited(puuids []string) {
	runner.mu.Lock()
	runner.state[settings.GameflowNamespace]["friendsToBeInvited"] = append([]string{}, puuids...)
	runner.mu.Unlock()
	runner.publishState()
}
func (runner *Runner) pendingFriends() []string {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	values, _ := runner.state[settings.GameflowNamespace]["friendsToBeInvited"].([]string)
	return append([]string{}, values...)
}
func (runner *Runner) removePendingFriend(puuid string) {
	runner.mu.Lock()
	values, _ := runner.state[settings.GameflowNamespace]["friendsToBeInvited"].([]string)
	runner.state[settings.GameflowNamespace]["friendsToBeInvited"] = slices.DeleteFunc(append([]string{}, values...), func(value string) bool { return value == puuid })
	runner.mu.Unlock()
	runner.publishState()
}

func (runner *Runner) invitePendingFriends(ctx context.Context) error {
	if len(runner.pendingFriends()) == 0 {
		return nil
	}
	var lobby *invitationLobby
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &lobby); err != nil {
		if missingResource(err) {
			runner.SetFriendsToBeInvited(nil)
			return nil
		}
		return err
	}
	if lobby == nil {
		runner.SetFriendsToBeInvited(nil)
		return nil
	}
	if !lobby.LocalMember.AllowedInviteOthers {
		return nil
	}
	var friends []friend
	if err := runner.get(ctx, "/lol-chat/v1/friends", &friends); err != nil {
		return err
	}
	for _, person := range friends {
		signature := stateSignature(person)
		if runner.friendStates[person.Puuid] == signature {
			continue
		}
		runner.friendStates[person.Puuid] = signature
		if err := runner.inviteFriend(ctx, person, *lobby); err != nil {
			return err
		}
	}
	return nil
}

func (runner *Runner) inviteFriend(ctx context.Context, person friend, lobby invitationLobby) error {
	if person.Puuid == "" || person.Availability != "chat" || !lobby.LocalMember.AllowedInviteOthers || !slices.Contains(runner.pendingFriends(), person.Puuid) {
		return nil
	}
	for _, member := range lobby.Members {
		if member.Puuid == person.Puuid {
			return nil
		}
	}
	if person.SummonerID <= 0 {
		return nil
	}
	defer runner.removePendingFriend(person.Puuid)
	if err := runner.write(ctx, settings.GameflowNamespace, "error-invite-friend", "POST", "/lol-lobby/v2/lobby/invitations", []any{map[string]any{"toSummonerId": person.SummonerID}}); err != nil {
		return err
	}
	_ = runner.customRoomMessage(ctx, fmt.Sprintf("[LeagueAkari-MyGo] 已向 %s #%s 发送房间邀请", person.GameName, person.GameTag))
	return nil
}

func (runner *Runner) HandleLCUEvent(parent context.Context, uri, eventType string, data any) error {
	if uri != "/lol-pre-end-of-game/v1/currentSequenceEvent" && !strings.HasPrefix(uri, "/lol-chat/v1/friends/") {
		return nil
	}
	runner.tickMu.Lock()
	defer runner.tickMu.Unlock()
	ctx, cancel := context.WithCancel(parent)
	runner.mu.Lock()
	runner.activeCancel = cancel
	runner.mu.Unlock()
	defer func() { cancel(); runner.mu.Lock(); runner.activeCancel = nil; runner.mu.Unlock() }()
	if uri == "/lol-pre-end-of-game/v1/currentSequenceEvent" {
		if eventType == "Delete" || data == nil {
			return nil
		}
		return runner.completeMissionCelebration(ctx, data)
	}
	var person friend
	if err := decodeJSON(data, &person); err != nil {
		return err
	}
	if eventType == "Delete" {
		runner.removePendingFriend(person.Puuid)
		delete(runner.friendStates, person.Puuid)
		return nil
	}
	if len(runner.pendingFriends()) == 0 {
		return nil
	}
	var lobby *invitationLobby
	if err := runner.get(ctx, "/lol-lobby/v2/lobby", &lobby); err != nil {
		return err
	}
	if lobby == nil {
		return nil
	}
	runner.friendStates[person.Puuid] = stateSignature(person)
	return runner.inviteFriend(ctx, person, *lobby)
}

func (runner *Runner) sendTeamSide(ctx context.Context, config GameflowSettings) error {
	var session selectSession
	if err := runner.get(ctx, "/lol-champ-select/v1/session", &session); err != nil {
		return err
	}
	if !session.BenchEnabled {
		return nil
	}
	var game gameSession
	if err := runner.get(ctx, "/lol-gameflow/v1/session", &game); err != nil {
		return err
	}
	mode := game.Map.GameMode
	if mode == "" {
		mode = game.GameData.Queue.GameMode
	}
	if mode != "ARAM" && mode != "KIWI" {
		return nil
	}
	team := 0
	for _, member := range session.MyTeam {
		if member.CellID == session.LocalPlayerCellID {
			team = member.Team
			break
		}
	}
	if team != 1 && team != 2 {
		return nil
	}
	conversation, err := runner.champSelectConversation(ctx)
	if err != nil {
		return err
	}
	if conversation == "" {
		return nil
	}
	signature := stateSignature(conversation, session.GameID, team, config.AutoSendARAMTeamSideVisibleToTeam)
	if runner.lastTeamSideState == signature {
		return nil
	}
	if runner.settings.Get(settings.GameflowNamespace, "autoSendARAMTeamSideEnabled") != true {
		return context.Canceled
	}
	message := "本局游戏你的队伍位于 🟦蓝色方 (↙️ 左下侧)"
	if team == 2 {
		message = "本局游戏你的队伍位于 🟥红色方 (↗️ 右上侧)"
	}
	kind := "chat"
	if !config.AutoSendARAMTeamSideVisibleToTeam {
		message = "[LeagueAkari-MyGo] " + message
		kind = "celebration"
	}
	runner.lastTeamSideState = signature
	return runner.chatMessage(ctx, conversation, message, kind)
}

func (runner *Runner) champSelectConversation(ctx context.Context) (string, error) {
	var conversations []struct {
		ID   string `json:"id"`
		Type string `json:"type"`
	}
	if err := runner.get(ctx, "/lol-chat/v1/conversations", &conversations); err != nil {
		return "", err
	}
	for _, conversation := range conversations {
		if conversation.Type == "championSelect" {
			return conversation.ID, nil
		}
	}
	return "", nil
}

func (runner *Runner) chatMessage(ctx context.Context, conversation, message, kind string) error {
	return runner.write(ctx, settings.SelectNamespace, "error-local-message", "POST", "/lol-chat/v1/conversations/"+url.PathEscape(conversation)+"/messages", map[string]any{"body": message, "fromPid": "", "fromSummonerId": 0, "id": conversation, "isHistorical": false, "timestamp": "", "type": kind})
}

func (runner *Runner) localMessage(ctx context.Context, key, message string) {
	if runner.lastLocalMessage == key {
		return
	}
	runner.lastLocalMessage = key
	conversation, err := runner.champSelectConversation(ctx)
	if err != nil || conversation == "" {
		runner.backloggedMessages = append(runner.backloggedMessages, message)
		return
	}
	for _, pending := range runner.backloggedMessages {
		_ = runner.chatMessage(ctx, conversation, "[LeagueAkari-MyGo] "+pending, "celebration")
	}
	runner.backloggedMessages = nil
	_ = runner.chatMessage(ctx, conversation, "[LeagueAkari-MyGo] "+message, "celebration")
}

func (runner *Runner) flushLocalMessages(ctx context.Context) {
	if len(runner.backloggedMessages) == 0 {
		return
	}
	conversation, err := runner.champSelectConversation(ctx)
	if err != nil || conversation == "" {
		return
	}
	for _, message := range runner.backloggedMessages {
		_ = runner.chatMessage(ctx, conversation, "[LeagueAkari-MyGo] "+message, "celebration")
	}
	runner.backloggedMessages = nil
}

func decodeJSON(value, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}
