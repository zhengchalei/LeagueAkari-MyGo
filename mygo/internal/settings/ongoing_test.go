package settings

import (
	"path/filepath"
	"reflect"
	"testing"
)

func TestOngoingCountsUseOriginalSetterTransformsAndNotifyConsumers(t *testing.T) {
	path := filepath.Join(t.TempDir(), "settings.json")
	store, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	store.ApplyDefaults(map[string]map[string]any{"ongoing-game-main": {"matchHistoryLoadCount": 50, "gameDetailsLoadCount": 20}})
	var notifications []string
	store.OnChange(func(namespace, key string) {
		if namespace == "ongoing-game-main" {
			notifications = append(notifications, key)
		}
	})
	for _, invalid := range []any{0, 201, "200", nil} {
		if err := store.Set("ongoing-game-main", "matchHistoryLoadCount", invalid); err != nil {
			t.Fatal(err)
		}
		if store.Get("ongoing-game-main", "matchHistoryLoadCount") != float64(50) {
			t.Fatalf("invalid count %v replaced the current choice", invalid)
		}
	}
	if len(notifications) != 0 {
		t.Fatal("rejected count notified consumers")
	}
	if err := store.Set("ongoing-game-main", "matchHistoryLoadCount", 200); err != nil {
		t.Fatal(err)
	}
	if err := store.Set("ongoing-game-main", "gameDetailsLoadCount", 200); err != nil {
		t.Fatal(err)
	}
	notifications = nil
	if err := store.Set("ongoing-game-main", "matchHistoryLoadCount", 10); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(notifications, []string{"matchHistoryLoadCount", "gameDetailsLoadCount"}) {
		t.Fatalf("dependent detail change not broadcast: %v", notifications)
	}
	reopened, err := New(path)
	if err != nil {
		t.Fatal(err)
	}
	if reopened.Get("ongoing-game-main", "gameDetailsLoadCount") != float64(10) {
		t.Fatal("lower detail count was not persisted")
	}
	for _, invalid := range []any{-1, 11, nil} {
		if err := store.Set("ongoing-game-main", "gameDetailsLoadCount", invalid); err != nil {
			t.Fatal(err)
		}
		if store.Get("ongoing-game-main", "gameDetailsLoadCount") != float64(10) {
			t.Fatalf("invalid details %v did not use the current history limit", invalid)
		}
	}
	if err := store.Set("ongoing-game-main", "gameDetailsLoadCount", 0); err != nil {
		t.Fatal(err)
	}
	if store.Get("ongoing-game-main", "gameDetailsLoadCount") != float64(0) {
		t.Fatal("explicitly disabled prefetch was not retained")
	}
}
