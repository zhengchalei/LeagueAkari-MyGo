package automation

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"slices"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type fakeClient struct {
	request func(context.Context, string, string, any) (any, error)
}

func (client fakeClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
	return client.request(ctx, method, path, body)
}

func newStore(t *testing.T) *settings.Store {
	t.Helper()
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func setSetting(t *testing.T, store *settings.Store, namespace, key string, value any) {
	t.Helper()
	if err := store.Set(namespace, key, value); err != nil {
		t.Fatal(err)
	}
}

func makeBallot(votes int) *HonorBallot {
	ballot := &HonorBallot{GameID: 123, EligibleAllies: []EligiblePlayer{{Puuid: "self"}, {Puuid: "premade"}, {Puuid: "random"}, {Puuid: "bot", BotPlayer: true}, {Puuid: "random"}}, EligibleOpponents: []EligiblePlayer{{Puuid: "opponent"}, {Puuid: "enemybot", BotPlayer: true}}}
	ballot.VotePool.Votes = votes
	return ballot
}

func TestHonorStrategyRulesAndEligibility(t *testing.T) {
	ballot := makeBallot(10)
	cases := []struct {
		strategy string
		expected []string
	}{
		{"prefer-lobby-member", []string{"premade", "random"}},
		{"only-lobby-member", []string{"premade"}},
		{"all-member", []string{"premade", "random"}},
		{"all-member-including-opponent", []string{"premade", "random", "opponent"}},
		{"opt-out", nil},
	}
	for _, item := range cases {
		t.Run(item.strategy, func(t *testing.T) {
			candidates := SelectHonorCandidates(item.strategy, ballot.EligibleAllies, ballot.EligibleOpponents, []string{"premade"}, map[string]bool{"self": true}, 10)
			if len(candidates) != len(item.expected) {
				t.Fatalf("got %v", candidates)
			}
			for _, expected := range item.expected {
				if !slices.Contains(candidates, expected) {
					t.Fatalf("missing %s in %v", expected, candidates)
				}
			}
			if item.strategy == "prefer-lobby-member" || item.strategy == "all-member-including-opponent" {
				if candidates[0] != "premade" {
					t.Fatal("premade priority lost")
				}
			}
		})
	}
	one := SelectHonorCandidates("prefer-lobby-member", ballot.EligibleAllies, ballot.EligibleOpponents, []string{"premade"}, map[string]bool{"self": true}, 1)
	if len(one) != 1 || one[0] != "premade" {
		t.Fatalf("vote limit or priority broken: %v", one)
	}
	already := SelectHonorCandidates("all-member", ballot.EligibleAllies, nil, nil, map[string]bool{"self": true, "premade": true}, 10)
	if len(already) != 1 || already[0] != "random" {
		t.Fatalf("already honored player not excluded: %v", already)
	}
}

func TestAutoHonorUsesTeammatesWithoutRoomDependencyAndCompletesOnce(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorStrategy", "all-member")
	ballot := makeBallot(2)
	var recipients []string
	completed, roomRequests := 0, 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "EndOfGame", nil
		case "/lol-honor-v2/v1/ballot/":
			return ballot, nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"puuid": "self"}, nil
		case "/lol-lobby/v2/party/eog-status":
			roomRequests++
			return nil, errors.New("room unavailable")
		case "/lol-honor/v1/honor":
			payload := body.(map[string]any)
			recipients = append(recipients, payload["recipientPuuid"].(string))
			return nil, nil
		case "/lol-honor/v1/ballot":
			completed++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected request %s %s", method, path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if roomRequests != 0 || completed != 1 || len(recipients) != 2 {
		t.Fatalf("rooms=%d completed=%d recipients=%v", roomRequests, completed, recipients)
	}
	if recipients[0] == recipients[1] || slices.Contains(recipients, "opponent") || slices.Contains(recipients, "bot") || slices.Contains(recipients, "self") {
		t.Fatalf("invalid recipients %v", recipients)
	}
}

func TestDisablingAutoHonorInterruptsRemainingVotesAndBallot(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorStrategy", "all-member")
	votes, completed := 0, 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "EndOfGame", nil
		case "/lol-honor-v2/v1/ballot/":
			return makeBallot(2), nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"puuid": "self"}, nil
		case "/lol-honor/v1/honor":
			votes++
			setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", false)
			return nil, nil
		case "/lol-honor/v1/ballot":
			completed++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); !errors.Is(err, context.Canceled) {
		t.Fatalf("expected canceled, got %v", err)
	}
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if votes != 1 || completed != 0 {
		t.Fatalf("votes=%d completed=%d", votes, completed)
	}
}

func TestSkipHonorsDoesNotQuerySummonerOrSubmitVotes(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorStrategy", "opt-out")
	completed := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "EndOfGame", nil
		case "/lol-honor-v2/v1/ballot/":
			return makeBallot(2), nil
		case "/lol-honor/v1/ballot":
			completed++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected request %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if completed != 1 {
		t.Fatal("skip did not complete ballot")
	}
}

func TestLiveManualHonorIsExcludedFromNextAutomaticVote(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorStrategy", "prefer-lobby-member")
	ballot := makeBallot(2)
	voted := []string{}
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "EndOfGame", nil
		case "/lol-honor-v2/v1/ballot/":
			return ballot, nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"puuid": "self"}, nil
		case "/lol-lobby/v2/party/eog-status":
			return map[string]any{"eogPlayers": []string{"premade"}}, nil
		case "/lol-honor/v1/honor":
			voted = append(voted, body.(map[string]any)["recipientPuuid"].(string))
			ballot.HonoredPlayers = append(ballot.HonoredPlayers, struct {
				RecipientPuuid string `json:"recipientPuuid"`
			}{RecipientPuuid: "random"})
			return nil, nil
		case "/lol-honor/v1/ballot":
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(voted) != 1 || voted[0] != "premade" {
		t.Fatalf("repeated manual vote or ignored priority: %v", voted)
	}
}

func TestOnlyPremadeDoesNotFallbackWhenRoomLookupFails(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoHonorStrategy", "only-lobby-member")
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method != "GET" {
			writes++
			return nil, nil
		}
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "EndOfGame", nil
		case "/lol-honor-v2/v1/ballot/":
			return makeBallot(2), nil
		case "/lol-summoner/v1/current-summoner":
			return map[string]any{"puuid": "self"}, nil
		case "/lol-lobby/v2/party/eog-status":
			return nil, errors.New("room lookup failed")
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err == nil {
		t.Fatal("expected room error")
	}
	if writes != 0 {
		t.Fatal("premade-only honored an unverified player")
	}
}

func TestReturnAndReconnectWaitForDelayAndDoNotRepeat(t *testing.T) {
	for _, operation := range []string{"return", "reconnect"} {
		t.Run(operation, func(t *testing.T) {
			store := newStore(t)
			phase, key, path := "EndOfGame", "playAgainEnabled", "/lol-lobby/v2/play-again"
			if operation == "reconnect" {
				phase, key, path = "Reconnect", "autoReconnectEnabled", "/lol-gameflow/v1/reconnect"
			}
			setSetting(t, store, settings.GameflowNamespace, key, true)
			writes := 0
			now := time.Unix(100, 0)
			client := fakeClient{request: func(ctx context.Context, method, requestPath string, body any) (any, error) {
				if requestPath == "/lol-gameflow/v1/gameflow-phase" {
					return phase, nil
				}
				if requestPath == "/lol-honor-v2/v1/ballot/" {
					return nil, nil
				}
				if requestPath == path && method == "POST" {
					writes++
					return nil, nil
				}
				return nil, fmt.Errorf("unexpected %s", requestPath)
			}}
			runner := New(client, store, nil)
			defer runner.Close()
			runner.now = func() time.Time { return now }
			if err := runner.Tick(context.Background()); err != nil {
				t.Fatal(err)
			}
			if writes != 0 {
				t.Fatal("executed before delay")
			}
			now = now.Add(11 * time.Second)
			for range 2 {
				if err := runner.Tick(context.Background()); err != nil {
					t.Fatal(err)
				}
			}
			if writes != 1 {
				t.Fatalf("expected one operation, got %d", writes)
			}
		})
	}
}

func TestAutoAcceptDelayCancelAndNewReadyCheck(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptDelaySeconds", 2)
	phase, accepted := "ReadyCheck", 0
	now := time.Unix(100, 0)
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return phase, nil
		case "/lol-matchmaking/v1/ready-check":
			return map[string]any{"playerResponse": "None", "state": "InProgress"}, nil
		case "/lol-matchmaking/v1/ready-check/accept":
			accepted++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	runner.now = func() time.Time { return now }
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if accepted != 0 {
		t.Fatal("accepted before countdown")
	}
	runner.CancelAutoAccept()
	now = now.Add(3 * time.Second)
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if accepted != 0 {
		t.Fatal("manual cancel did not suppress same ready check")
	}
	phase = "Lobby"
	_ = runner.Tick(context.Background())
	phase = "ReadyCheck"
	_ = runner.Tick(context.Background())
	now = now.Add(3 * time.Second)
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if accepted != 1 {
		t.Fatalf("expected one accept in new ready check, got %d", accepted)
	}
}

func TestAutomaticWritesRequireFeatureEnabledAndCurrentPhase(t *testing.T) {
	store := newStore(t)
	writes := 0
	phaseReads := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method != "GET" {
			writes++
			return nil, nil
		}
		if path == "/lol-gameflow/v1/gameflow-phase" {
			phaseReads++
			if phaseReads == 1 {
				return "ReadyCheck", nil
			}
			return "ChampSelect", nil
		}
		if path == "/lol-matchmaking/v1/ready-check" {
			return map[string]any{"playerResponse": "None"}, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 0 {
		t.Fatal("default settings caused writes")
	}
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptEnabled", true)
	phaseReads = 0
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if writes != 0 {
		t.Fatal("phase changed before submission but write happened")
	}
}

func TestMatchmakingRequiresLeaderAndSettledInvitations(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoMatchmakingDelaySeconds", 0)
	lobby := map[string]any{"canStartActivity": true, "partyId": "party", "localMember": map[string]any{"isLeader": true, "allowedStartActivity": true}, "members": []any{map[string]any{}}, "invitations": []any{map[string]any{"state": "Pending"}}, "gameConfig": map[string]any{"isCustom": false}}
	started := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "Lobby", nil
		case "/lol-lobby/v2/lobby":
			return lobby, nil
		case "/lol-matchmaking/v1/search":
			return map[string]any{"isCurrentlyInQueue": false}, nil
		case "/lol-lobby/v2/lobby/matchmaking/search":
			started++
			return nil, nil
		default:
			return nil, fmt.Errorf("unexpected %s", path)
		}
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if started != 0 {
		t.Fatal("started while invite pending")
	}
	lobby["invitations"] = []any{}
	lobby["localMember"].(map[string]any)["isLeader"] = false
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if started != 0 {
		t.Fatal("non-leader started queue")
	}
	lobby["localMember"].(map[string]any)["isLeader"] = true
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if started != 1 {
		t.Fatalf("expected one queue start, got %d", started)
	}
}

func decodeValue[T any](value any) T {
	data, _ := json.Marshal(value)
	var result T
	_ = json.Unmarshal(data, &result)
	return result
}
