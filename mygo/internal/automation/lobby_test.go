package automation

import (
	"context"
	"fmt"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func invitation(id, gameType string, canAccept bool) receivedInvitation {
	return decodeValue[receivedInvitation](map[string]any{"invitationId": id, "state": "Pending", "canAcceptInvitation": canAccept, "gameConfig": map[string]any{"inviteGameType": gameType}})
}

func TestInvitationsPreferAcceptUseGameRuleAndHandleStateChanges(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHandleInvitationsEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "invitationHandlingStrategies", map[string]string{"<DEFAULT>": "decline", "ARAM_UNRANKED_5x5": "accept"})
	invitations := []receivedInvitation{invitation("unknown", "NORMAL", true), invitation("aram", "ARAM_UNRANKED_5x5", true), invitation("blocked", "ARAM_UNRANKED_5x5", false)}
	var paths []string
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			paths = append(paths, path)
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "None", nil
		case "/lol-lobby/v2/received-invitations":
			return invitations, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(paths) != 1 || paths[0] != "/lol-lobby/v2/received-invitations/aram/accept" {
		t.Fatalf("priority/duplicate issue %v", paths)
	}
	invitations[1].State = "Accepted"
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(paths) != 2 || paths[1] != "/lol-lobby/v2/received-invitations/unknown/decline" {
		t.Fatalf("default fallback failed %v", paths)
	}
}

func TestAwayInvitationOptionSuppressesWithoutDeclining(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHandleInvitationsEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "rejectInvitationWhenAway", true)
	setSetting(t, store, settings.GameflowNamespace, "invitationHandlingStrategies", map[string]string{"<DEFAULT>": "accept"})
	availability := "away"
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "None", nil
		case "/lol-lobby/v2/received-invitations":
			return []receivedInvitation{invitation("invite", "NORMAL", true)}, nil
		case "/lol-chat/v1/me":
			return map[string]any{"availability": availability}, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 0 {
		t.Fatal("away setting must suppress handling, not actively decline")
	}
	availability = "chat"
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 1 {
		t.Fatal("returning online did not handle invite")
	}
}

func TestInvitationsStopWhenDisabledDuringLookup(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHandleInvitationsEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "invitationHandlingStrategies", map[string]string{"<DEFAULT>": "accept"})
	reads, writes := 0, 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return "None", nil
		}
		if path == "/lol-lobby/v2/received-invitations" {
			reads++
			if reads == 2 {
				setSetting(t, store, settings.GameflowNamespace, "autoHandleInvitationsEnabled", false)
			}
			return []receivedInvitation{invitation("invite", "NORMAL", true)}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	_ = runner.Tick(context.Background())
	if writes != 0 {
		t.Fatal("submitted invite after disabling")
	}
}

func TestLeaderTransferPrefersReadyExcludesSelfSpectatorsAndDoesNotRepeat(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoSkipLeaderEnabled", true)
	lobby := map[string]any{"localMember": map[string]any{"isLeader": true, "summonerId": 1}, "members": []any{map[string]any{"summonerId": 1, "ready": true}, map[string]any{"summonerId": 2, "ready": false}, map[string]any{"summonerId": 3, "ready": true}, map[string]any{"summonerId": 4, "ready": true, "isSpectator": true}}}
	var paths []string
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			paths = append(paths, path)
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "Lobby", nil
		case "/lol-lobby/v2/lobby":
			return lobby, nil
		case "/lol-chat/v1/conversations":
			return []any{}, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if len(paths) != 1 || paths[0] != "/lol-lobby/v2/lobby/members/3/promote" {
		t.Fatalf("unexpected transfer %v", paths)
	}
	lobby["localMember"].(map[string]any)["isLeader"] = false
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(paths) != 1 {
		t.Fatal("nonleader transferred ownership")
	}
}

func TestLeaderTransferStopsWhenCandidateLeaves(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoSkipLeaderEnabled", true)
	reads, writes := 0, 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "POST" {
			writes++
			return nil, nil
		}
		if path == "/lol-gameflow/v1/gameflow-phase" {
			return "Lobby", nil
		}
		if path == "/lol-lobby/v2/lobby" {
			reads++
			members := []any{}
			if reads == 1 {
				members = append(members, map[string]any{"summonerId": 2, "ready": true})
			}
			return map[string]any{"localMember": map[string]any{"isLeader": true, "summonerId": 1}, "members": members}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 0 {
		t.Fatal("promoted member who already left")
	}
}

func queueData(time, estimate, penalty float64) map[string]any {
	return map[string]any{"lobbyId": "lobby", "queueId": 450, "searchState": "Searching", "isCurrentlyInQueue": true, "timeInQueue": time, "estimatedQueueTime": estimate, "lowPriorityData": map[string]any{"penaltyTime": penalty}}
}

func TestQueueRestartSubtractsInitialPenaltyAndCancelsOnce(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingRematchStrategy", "fixed-duration")
	setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingRematchFixedDuration", 30)
	search := queueData(60, 90, 60)
	cancels := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "DELETE" {
			cancels++
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "Matchmaking", nil
		case "/lol-matchmaking/v1/search":
			return search, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if cancels != 0 {
		t.Fatal("penalty counted as matchmaking duration")
	}
	search = queueData(90, 90, 0)
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if cancels != 1 {
		t.Fatalf("expected single cancel after initial penalty, got %d", cancels)
	}
	search = queueData(1, 90, 0)
	_ = runner.Tick(context.Background())
	search = queueData(31, 90, 0)
	_ = runner.Tick(context.Background())
	if cancels != 2 {
		t.Fatal("new queue attempt did not reset cancellation")
	}
}

func TestEstimatedQueueRestartRechecksReadyCheckAndCancellationSetting(t *testing.T) {
	for _, mode := range []string{"ready-check", "disabled", "estimated"} {
		t.Run(mode, func(t *testing.T) {
			store := newStore(t)
			setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingRematchStrategy", "estimated-duration")
			phase := "Matchmaking"
			reads, cancels := 0, 0
			client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
				if method == "DELETE" {
					cancels++
					return nil, nil
				}
				if path == "/lol-gameflow/v1/gameflow-phase" {
					return phase, nil
				}
				if path == "/lol-matchmaking/v1/search" {
					reads++
					if reads == 2 {
						if mode == "ready-check" {
							phase = "ReadyCheck"
						}
						if mode == "disabled" {
							setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingRematchStrategy", "never")
						}
					}
					return queueData(60, 60, 0), nil
				}
				return nil, fmt.Errorf("unexpected %s", path)
			}}
			runner := New(client, store, nil)
			defer runner.Close()
			_ = runner.Tick(context.Background())
			expected := 0
			if mode == "estimated" {
				expected = 1
			}
			if cancels != expected {
				t.Fatalf("cancels=%d expected=%d", cancels, expected)
			}
		})
	}
}
