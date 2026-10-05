package automation

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestPendingOfflineFriendIsInvitedOnceOnComingOnline(t *testing.T) {
	store := newStore(t)
	availability := "offline"
	invites := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "Lobby", nil
		case "/lol-lobby/v2/lobby":
			return map[string]any{"localMember": map[string]any{"allowedInviteOthers": true}, "members": []any{}}, nil
		case "/lol-chat/v1/friends":
			return []friend{{Puuid: "selected", SummonerID: 12, Availability: availability}, {Puuid: "other", SummonerID: 13, Availability: "chat"}}, nil
		case "/lol-lobby/v2/lobby/invitations":
			invites++
			if body.([]any)[0].(map[string]any)["toSummonerId"] != int64(12) {
				t.Fatal(body)
			}
			return nil, nil
		case "/lol-chat/v1/conversations":
			return []any{}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	runner.SetFriendsToBeInvited([]string{"selected"})
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if invites != 0 {
		t.Fatal("offline friend invited")
	}
	availability = "chat"
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if invites != 1 || len(runner.pendingFriends()) != 0 {
		t.Fatalf("invites=%d pending=%v", invites, runner.pendingFriends())
	}
}

func TestFriendEventsRespectLobbyPermissionMembershipAndDeletion(t *testing.T) {
	store := newStore(t)
	allowed, inRoom := false, false
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method != "GET" {
			writes++
			return nil, nil
		}
		if path == "/lol-lobby/v2/lobby" {
			members := []any{}
			if inRoom {
				members = append(members, map[string]any{"puuid": "friend"})
			}
			return map[string]any{"localMember": map[string]any{"allowedInviteOthers": allowed}, "members": members}, nil
		}
		return []any{}, nil
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	runner.SetFriendsToBeInvited([]string{"friend"})
	person := friend{Puuid: "friend", Availability: "chat", SummonerID: 12}
	event := func(kind string) {
		t.Helper()
		if err := runner.HandleLCUEvent(context.Background(), "/lol-chat/v1/friends/12", kind, person); err != nil {
			t.Fatal(err)
		}
	}
	event("Update")
	allowed = true
	inRoom = true
	event("Update")
	event("Delete")
	inRoom = false
	event("Update")
	if writes != 0 || len(runner.pendingFriends()) != 0 {
		t.Fatal("invited without permission, in-room, or deleted friend")
	}
}

func TestPendingFriendsAreClearedAfterLeavingLobby(t *testing.T) {
	runner := New(fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return "None", nil
		}
		return nil, errors.New("HTTP 404 lobby absent")
	}}, newStore(t), nil)
	defer runner.Close()
	runner.SetFriendsToBeInvited([]string{"friend"})
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(runner.pendingFriends()) != 0 {
		t.Fatal("pending invite survived leaving lobby")
	}
}

func TestARAMTeamSideSupportsCellZeroAndOriginalVisibilityRule(t *testing.T) {
	for _, visible := range []bool{false, true} {
		t.Run(fmt.Sprint(visible), func(t *testing.T) {
			store := newStore(t)
			setSetting(t, store, settings.GameflowNamespace, "autoSendARAMTeamSideEnabled", true)
			setSetting(t, store, settings.GameflowNamespace, "autoSendARAMTeamSideVisibleToTeam", visible)
			session := championSession()
			session.BenchEnabled = true
			session.MyTeam[0].Team = 2
			session.Actions = nil
			var messages []map[string]any
			client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
				if method == "POST" {
					messages = append(messages, body.(map[string]any))
					return nil, nil
				}
				switch path {
				case "/lol-gameflow/v1/gameflow-phase":
					return "ChampSelect", nil
				case "/lol-champ-select/v1/session":
					return session, nil
				case "/lol-gameflow/v1/session":
					return map[string]any{"map": map[string]any{"gameMode": "KIWI"}}, nil
				case "/lol-chat/v1/conversations":
					return []any{map[string]any{"id": "select", "type": "championSelect"}}, nil
				}
				return nil, fmt.Errorf("unexpected %s", path)
			}}
			runner := New(client, store, nil)
			defer runner.Close()
			for range 2 {
				if err := runner.Tick(context.Background()); err != nil {
					t.Fatal(err)
				}
			}
			if len(messages) != 1 || !strings.Contains(messages[0]["body"].(string), "红色方") {
				t.Fatalf("messages=%v", messages)
			}
			expected := "celebration"
			if visible {
				expected = "chat"
			}
			if messages[0]["type"] != expected {
				t.Fatal("visibility changed")
			}
		})
	}
}

func TestTradeUsesPreferenceAndDoesNotRepeatWithDisabledAutoPick(t *testing.T) {
	for _, item := range []struct {
		current, requested int
		priority           bool
		action             string
	}{{2, 1, true, "accept"}, {2, 1, false, "decline"}, {99, 1, false, "accept"}, {2, 99, true, "decline"}} {
		t.Run(fmt.Sprintf("%d:%d:%t", item.current, item.requested, item.priority), func(t *testing.T) {
			store := newStore(t)
			configurePick(t, store, map[string]any{"enabled": false, "benchHandleTradeEnabled": true, "benchSelectFirstAvailableChampion": item.priority, "champions": map[string]any{"default": []int{1, 2}}})
			session := championSession()
			session.BenchEnabled = true
			session.Actions = nil
			session.MyTeam[0].ChampionID = item.current
			writes := 0
			client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
				if method == "POST" {
					writes++
					if !strings.HasSuffix(path, "/"+item.action) {
						t.Fatal(path)
					}
					return nil, nil
				}
				switch path {
				case "/lol-gameflow/v1/gameflow-phase":
					return "ChampSelect", nil
				case "/lol-champ-select/v1/session":
					return session, nil
				case "/lol-gameflow/v1/session":
					return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
				case "/lol-champ-select/v1/ongoing-champion-swap":
					return championSwap{ID: 10, State: "RECEIVED", RequesterChampionID: item.requested}, nil
				case "/lol-chat/v1/conversations":
					return []any{}, nil
				}
				return nil, fmt.Errorf("unexpected %s", path)
			}}
			runner := New(client, store, nil)
			defer runner.Close()
			for range 2 {
				if err := runner.Tick(context.Background()); err != nil {
					t.Fatal(err)
				}
			}
			if writes != 1 {
				t.Fatalf("writes=%d", writes)
			}
		})
	}
}

func TestTradeDelayAndTemporaryDisableStopPendingDecision(t *testing.T) {
	store := newStore(t)
	configurePick(t, store, map[string]any{"enabled": false, "benchHandleTradeEnabled": true, "delaySeconds": 3})
	session := championSession()
	session.BenchEnabled = true
	session.Actions = nil
	session.MyTeam[0].ChampionID = 9
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ChampSelect", nil
		case "/lol-champ-select/v1/session":
			return session, nil
		case "/lol-gameflow/v1/session":
			return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": "ARAM"}}}, nil
		case "/lol-champ-select/v1/ongoing-champion-swap":
			return championSwap{ID: 1, State: "RECEIVED", RequesterChampionID: 1}, nil
		case "/lol-chat/v1/conversations":
			return []any{}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	now := time.Unix(100, 0)
	runner.now = func() time.Time { return now }
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	state := runner.State()[settings.SelectNamespace].(map[string]any)["delayedChampionSwap"].(map[string]any)
	if writes != 0 || state["action"] != "accept" || state["tradeId"] != float64(1) {
		t.Fatal(state)
	}
	runner.SetTemporarilyDisabled(true)
	now = now.Add(4 * time.Second)
	_ = runner.Tick(context.Background())
	if writes != 0 || runner.State()[settings.SelectNamespace].(map[string]any)["delayedChampionSwap"] != nil {
		t.Fatal("disabled trade continued")
	}
}

func TestCloneVoteAndArenaBraveryUseOnlyOwnAction(t *testing.T) {
	for _, item := range []struct {
		mode, action string
		champion     int
	}{{"ONEFORALL", "vote", 2}, {"CHERRY", "pick", -3}, {"ARAM", "pick", 2}} {
		t.Run(item.mode, func(t *testing.T) {
			store := newStore(t)
			setSetting(t, store, settings.SelectNamespace, "pickConfig", map[string]any{item.mode: map[string]any{"enabled": true, "strategy": "lock-in-immediately", "champions": map[string]any{"default": []int{-3, 2}}}})
			session := championSession()
			session.Actions[0][0].Type = item.action
			var patches []map[string]any
			client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
				if method == "PATCH" {
					patches = append(patches, body.(map[string]any))
					return nil, nil
				}
				switch path {
				case "/lol-gameflow/v1/gameflow-phase":
					return "ChampSelect", nil
				case "/lol-gameflow/v1/session":
					return map[string]any{"gameData": map[string]any{"queue": map[string]any{"gameMode": item.mode}}}, nil
				case "/lol-champ-select/v1/session":
					return session, nil
				case "/lol-champ-select/v1/pickable-champion-ids":
					return []int{2}, nil
				case "/lol-champ-select/v1/all-grid-champions":
					return []any{map[string]any{"id": 2}}, nil
				case "/lol-chat/v1/conversations":
					return []any{}, nil
				}
				return nil, fmt.Errorf("unexpected %s", path)
			}}
			runner := New(client, store, nil)
			defer runner.Close()
			for range 2 {
				if err := runner.Tick(context.Background()); err != nil {
					t.Fatal(err)
				}
			}
			if len(patches) != 1 || patches[0]["championId"] != item.champion || patches[0]["type"] != item.action {
				t.Fatalf("patches=%v", patches)
			}
		})
	}
}

func TestLocalMessagesBacklogUntilSelectChatAndFlushOnce(t *testing.T) {
	available := false
	var messages []map[string]any
	runner := New(fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			messages = append(messages, body.(map[string]any))
			return nil, nil
		}
		if available {
			return []any{map[string]any{"id": "select", "type": "championSelect"}}, nil
		}
		return []any{}, nil
	}}, newStore(t), nil)
	defer runner.Close()
	runner.localMessage(context.Background(), "key", "pending")
	runner.localMessage(context.Background(), "key", "pending")
	available = true
	runner.flushLocalMessages(context.Background())
	runner.flushLocalMessages(context.Background())
	if len(messages) != 1 || messages[0]["type"] != "celebration" || messages[0]["body"] != "[LeagueAkari-MyGo] pending" {
		t.Fatal(messages)
	}
}
