package main

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

func TestStartupOngoingDefaultsAndExistingChoicesArePreserved(t *testing.T) {
	for _, existing := range []bool{false, true} {
		name := "fresh-profile"
		if existing {
			name = "existing-profile-without-memory-migration-marker"
		}
		t.Run(name, func(t *testing.T) {
			dir := t.TempDir()
			wanted := map[string]int64{"matchHistoryLoadCount": 50, "concurrency": 4, "gameDetailsLoadCount": 20}
			if existing {
				store, err := settings.New(filepath.Join(dir, "settings.json"))
				if err != nil {
					t.Fatal(err)
				}
				wanted = map[string]int64{"matchHistoryLoadCount": 200, "concurrency": 24, "gameDetailsLoadCount": 0}
				for key, value := range wanted {
					if err := store.Set("ongoing-game-main", key, value); err != nil {
						t.Fatal(err)
					}
				}
			}
			// Initialization creates services only: no Start/PollOnce, discovery, LCU, or migration.
			d := &Desktop{}
			if err := d.initializeWithDirectory(context.Background(), dir); err != nil {
				t.Fatal(err)
			}
			defer d.externalClient.CloseIdleConnections()
			defer d.platform.Close()
			defer d.player.Close()
			defer d.game.Close()
			for key, value := range wanted {
				if actual := client.Number(d.store.Get("ongoing-game-main", key)); actual != value {
					t.Fatalf("startup changed %s from %d to %d", key, value, actual)
				}
			}
			if d.store.Get("mygo-main", "memorySettingsMigrated") == true {
				t.Fatal("startup unexpectedly applied the old memory preset")
			}
		})
	}
}
