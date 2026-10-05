package automation

import (
	"context"
	"fmt"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestMissionCelebrationIsSkippedOnlyWithPlayAgainInPreEndOfGame(t *testing.T) {
	for _, item := range []struct {
		enabled     bool
		phase, name string
		expected    int
	}{{false, "PreEndOfGame", "missions-celebration", 0}, {true, "InProgress", "missions-celebration", 0}, {true, "PreEndOfGame", "other", 0}, {true, "PreEndOfGame", "missions-celebration", 1}} {
		t.Run(fmt.Sprintf("%t:%s:%s", item.enabled, item.phase, item.name), func(t *testing.T) {
			store := newStore(t)
			setSetting(t, store, settings.GameflowNamespace, "playAgainEnabled", item.enabled)
			writes := 0
			runner := New(fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
				if method == "GET" {
					return item.phase, nil
				}
				if path != "/lol-pre-end-of-game/v1/complete/missions-celebration" {
					t.Fatal(path)
				}
				writes++
				return nil, nil
			}}, store, nil)
			defer runner.Close()
			for range 2 {
				if err := runner.HandleLCUEvent(context.Background(), "/lol-pre-end-of-game/v1/currentSequenceEvent", "Update", map[string]any{"name": item.name}); err != nil {
					t.Fatal(err)
				}
			}
			if writes != item.expected {
				t.Fatalf("writes=%d", writes)
			}
		})
	}
}

func TestMissionPollingAndEventDoNotRepeatSkipAndResetForNextGame(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "playAgainEnabled", true)
	phase := "PreEndOfGame"
	writes := 0
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return phase, nil
		case "/lol-pre-end-of-game/v1/currentSequenceEvent":
			return map[string]any{"name": "missions-celebration"}, nil
		case "/lol-pre-end-of-game/v1/complete/missions-celebration":
			writes++
			return nil, nil
		case "/lol-honor-v2/v1/ballot/":
			return nil, nil
		}
		return nil, fmt.Errorf("unexpected %s", path)
	}}
	runner := New(client, store, nil)
	defer runner.Close()
	_ = runner.HandleLCUEvent(context.Background(), "/lol-pre-end-of-game/v1/currentSequenceEvent", "Create", map[string]any{"name": "missions-celebration"})
	for range 2 {
		if err := runner.Tick(context.Background()); err != nil {
			t.Fatal(err)
		}
	}
	if writes != 1 {
		t.Fatalf("duplicate writes=%d", writes)
	}
	phase = "None"
	_ = runner.Tick(context.Background())
	phase = "PreEndOfGame"
	_ = runner.Tick(context.Background())
	if writes != 2 {
		t.Fatal("next game skip did not reset")
	}
}

func TestDisablingPlayAgainDuringMissionCheckCancelsSkip(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "playAgainEnabled", true)
	writes := 0
	runner := New(fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if method == "GET" {
			setSetting(t, store, settings.GameflowNamespace, "playAgainEnabled", false)
			return "PreEndOfGame", nil
		}
		writes++
		return nil, nil
	}}, store, nil)
	defer runner.Close()
	_ = runner.HandleLCUEvent(context.Background(), "/lol-pre-end-of-game/v1/currentSequenceEvent", "Update", map[string]any{"name": "missions-celebration"})
	if writes != 0 {
		t.Fatal("disabled skip was submitted")
	}
}
