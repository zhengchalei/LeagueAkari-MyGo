package automation

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type stateUpdate struct {
	namespace string
	key       string
	value     any
}

func stateUpdates(t *testing.T, updates *[]stateUpdate) func(string, string, ...any) {
	t.Helper()
	return func(namespace, name string, args ...any) {
		if namespace != "mobx-utils-main" {
			return
		}
		if len(args) != 3 || !reflect.DeepEqual(args[2], map[string]any{"action": "update", "raw": true}) {
			t.Fatalf("invalid state update: %s %v", name, args)
		}
		*updates = append(*updates, stateUpdate{namespace: name, key: args[0].(string), value: args[1]})
	}
}

func lastStateValue(t *testing.T, updates []stateUpdate, namespace, key string) any {
	t.Helper()
	for index := len(updates) - 1; index >= 0; index-- {
		update := updates[index]
		if update.namespace == "update-state-prop/"+namespace+":state" && update.key == key {
			return update.value
		}
	}
	t.Fatalf("no state update for %s.%s: %v", namespace, key, updates)
	return nil
}

func TestCountdownStatePublishesOnlyChangesAndCancelPublishesImmediately(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptDelaySeconds", 2)
	client := fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		switch path {
		case "/lol-gameflow/v1/gameflow-phase":
			return "ReadyCheck", nil
		case "/lol-matchmaking/v1/ready-check":
			return map[string]any{"playerResponse": "None", "state": "InProgress"}, nil
		default:
			return nil, fmt.Errorf("unexpected %s %s", method, path)
		}
	}}
	updates := []stateUpdate{}
	runner := New(client, store, stateUpdates(t, &updates))
	defer runner.Close()
	runner.now = func() time.Time { return time.Unix(100, 0) }
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if lastStateValue(t, updates, settings.GameflowNamespace, "willAccept") != true || lastStateValue(t, updates, settings.GameflowNamespace, "willAcceptAt") != float64(102000) {
		t.Fatal("ready-check countdown was not published")
	}
	count := len(updates)
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	if len(updates) != count {
		t.Fatal("unchanged polling published duplicate state")
	}
	runner.CancelAutoAccept()
	if lastStateValue(t, updates, settings.GameflowNamespace, "willAccept") != false || lastStateValue(t, updates, settings.GameflowNamespace, "willAcceptAt") != float64(-1) {
		t.Fatal("cancel required another tick to publish")
	}
	count = len(updates)
	runner.CancelAutoAccept()
	if len(updates) != count {
		t.Fatal("repeated cancel published duplicate state")
	}
}

func TestPendingFriendStatePublishesSetDeleteAndCancelWithoutPolling(t *testing.T) {
	updates := []stateUpdate{}
	runner := New(fakeClient{request: func(context.Context, string, string, any) (any, error) {
		t.Fatal("friend state updates must not query LCU")
		return nil, nil
	}}, newStore(t), stateUpdates(t, &updates))
	defer runner.Close()
	runner.SetFriendsToBeInvited([]string{"first", "second"})
	first := lastStateValue(t, updates, settings.GameflowNamespace, "friendsToBeInvited")
	if !reflect.DeepEqual(first, []any{"first", "second"}) {
		t.Fatal(first)
	}
	if err := runner.HandleLCUEvent(context.Background(), "/lol-chat/v1/friends/1", "Delete", friend{Puuid: "first"}); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(lastStateValue(t, updates, settings.GameflowNamespace, "friendsToBeInvited"), []any{"second"}) || !reflect.DeepEqual(first, []any{"first", "second"}) {
		t.Fatal("deletion was not published independently of the previous snapshot")
	}
	runner.SetFriendsToBeInvited(nil)
	if !reflect.DeepEqual(lastStateValue(t, updates, settings.GameflowNamespace, "friendsToBeInvited"), []any{}) {
		t.Fatal("cancelled invitations were not published")
	}
}

func TestTemporaryDisablePublishesAndClearsPendingSelectionImmediately(t *testing.T) {
	updates := []stateUpdate{}
	runner := New(fakeClient{}, newStore(t), stateUpdates(t, &updates))
	defer runner.Close()
	runner.SetSelectGroups([]SelectGroup{{}})
	if got := lastStateValue(t, updates, settings.SelectNamespace, "groups"); len(got.([]any)) != 1 {
		t.Fatal("selection groups were not published")
	}
	runner.mu.Lock()
	runner.state[settings.SelectNamespace]["delayedPick"] = map[string]any{"championId": 1}
	runner.state[settings.SelectNamespace]["delayedChampionSwap"] = map[string]any{"id": 2}
	runner.mu.Unlock()
	runner.publishState()
	runner.SetTemporarilyDisabled(true)
	if lastStateValue(t, updates, settings.SelectNamespace, "temporarilyDisabled") != true || lastStateValue(t, updates, settings.SelectNamespace, "delayedPick") != nil || lastStateValue(t, updates, settings.SelectNamespace, "delayedChampionSwap") != nil {
		t.Fatal("temporary disable left stale selection countdowns")
	}
}

func TestFailedPhaseReadPublishesClearedCountdown(t *testing.T) {
	store := newStore(t)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptEnabled", true)
	setSetting(t, store, settings.GameflowNamespace, "autoAcceptDelaySeconds", 2)
	fail := false
	updates := []stateUpdate{}
	runner := New(fakeClient{request: func(ctx context.Context, method, path string, body any) (any, error) {
		if path == "/lol-gameflow/v1/gameflow-phase" {
			if fail {
				return nil, errors.New("client disconnected")
			}
			return "ReadyCheck", nil
		}
		return map[string]any{"playerResponse": "None", "state": "InProgress"}, nil
	}}, store, stateUpdates(t, &updates))
	defer runner.Close()
	if err := runner.Tick(context.Background()); err != nil {
		t.Fatal(err)
	}
	fail = true
	if err := runner.Tick(context.Background()); err == nil {
		t.Fatal("expected disconnected client error")
	}
	if lastStateValue(t, updates, settings.GameflowNamespace, "willAccept") != false || lastStateValue(t, updates, settings.GameflowNamespace, "willAcceptAt") != float64(-1) {
		t.Fatal("error return did not publish cleared countdown")
	}
}
