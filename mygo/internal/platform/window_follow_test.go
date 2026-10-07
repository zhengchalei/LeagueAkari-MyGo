package platform

import (
	"context"
	"testing"
	"time"
)

func TestTimerFollowsSupportedModeChangesWithoutPhaseChange(t *testing.T) {
	store := testStore(t)
	ns := "window-manager-main/cd-timer-window"
	storeSet(t, store, ns, "enabled", true)
	queue := map[string]any{"gameMode": "CLASSIC"}
	backend := &fakeClient{state: map[string]any{"state": map[string]any{"connectionState": "connected"}, "gameflow": map[string]any{"phase": "InProgress", "session": map[string]any{"gameData": map[string]any{"queue": queue}}}}}
	var actions []string
	service := New(Options{Native: gameNative(), Store: store, Client: backend, WindowAction: func(name, method string, _ []any) (any, error) {
		if name == "cd-timer-window" {
			actions = append(actions, method)
		}
		return nil, nil
	}})
	defer service.Close()
	service.lastTimerPoll = time.Now()
	service.followWindows(context.Background())
	service.followWindows(context.Background())
	queue["gameMode"] = "TFT"
	service.followWindows(context.Background())
	queue["gameMode"] = "ARAM"
	service.followWindows(context.Background())
	if len(actions) != 3 || actions[0] != "show" || actions[1] != "hide" || actions[2] != "show" {
		t.Fatal("timer did not follow current supported mode", actions)
	}
	service.set(ns, "gameTime", 120)
	storeSet(t, store, ns, "enabled", false)
	if service.State(ns)["gameTime"] != nil {
		t.Fatal("disabled timer retained live game time")
	}
}

func TestDisabledWindowShortcutsCannotRecreateWindows(t *testing.T) {
	for _, name := range []string{"opgg-window", "ongoing-game-window", "cd-timer-window"} {
		t.Run(name, func(t *testing.T) {
			store := testStore(t)
			ns := "window-manager-main/" + name
			storeSet(t, store, ns, "enabled", false)
			storeSet(t, store, ns, "showShortcut", "Control+A")
			var actions []string
			service := New(Options{Native: gameNative(), Store: store, WindowAction: func(window, method string, _ []any) (any, error) {
				if window == name {
					actions = append(actions, method)
				}
				return nil, nil
			}})
			defer service.Close()
			service.applyShortcutSettings()
			service.handleKey(162, true)
			service.handleKey(65, true)
			service.handleKey(65, false)
			service.handleKey(162, false)
			if len(actions) != 0 {
				t.Fatal("disabled shortcut opened a window", actions)
			}
			storeSet(t, store, ns, "enabled", true)
			actions = nil
			service.handleKey(162, true)
			service.handleKey(65, true)
			service.handleKey(65, false)
			service.handleKey(162, false)
			if len(actions) == 0 {
				t.Fatal("enabled shortcut stopped working")
			}
		})
	}
}
