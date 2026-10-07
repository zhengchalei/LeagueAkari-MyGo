package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/automation"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

// Opt in on a desktop host: this exercises the actual auxiliary-window factory,
// never connects to an LCU and keeps layout/configuration in a temporary directory.
func TestMain(m *testing.M) {
	if os.Getenv("NATIVE_MINI_WINDOW_TEST") != "" {
		if err := nativeMiniWindowSmoke(); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func nativeMiniWindowSmoke() error {
	dir := os.Getenv("NATIVE_MINI_WINDOW_TEST")
	if dir == "" {
		return fmt.Errorf("requires a Windows desktop session")
	}
	temporary, err := os.MkdirTemp("", "native-mini-layout-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(temporary)
	store, err := settings.New(filepath.Join(temporary, "settings.json"))
	if err != nil {
		return err
	}
	defaults := map[string]map[string]any{}
	_ = json.Unmarshal(defaultSettings, &defaults)
	store.ApplyDefaults(defaults)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	d := &Desktop{ctx: ctx, store: store, client: client.New(nil), windows: map[string]*mygo.Window{}, static: staticStates()}
	d.automation = automation.New(d.client, store, nil)
	result := make(chan error, 1)
	mygo.App.SetName("Native Mini lifecycle test")
	mygo.App.WhenReady(func() {
		win := d.openWindow("aux-window")
		go func() {
			finish := func(err error) { result <- err; mygo.App.Quit() }
			if win == nil || win.Page() != nil {
				finish(fmt.Errorf("Mini unexpectedly created a web page"))
				return
			}
			time.Sleep(500 * time.Millisecond)
			win.SetSize(380, 640)
			win.Close() // enabled Mini closes to hidden, retaining the native surface.
			time.Sleep(200 * time.Millisecond)
			if win.IsDestroyed() || d.state("window-manager-main/aux-window", "state")["show"] == true {
				finish(fmt.Errorf("close did not retain a hidden Mini"))
				return
			}
			if reopened := d.openWindow("aux-window"); reopened != win {
				finish(fmt.Errorf("reopening created a second Mini"))
				return
			}
			time.Sleep(300 * time.Millisecond)
			data, err := win.CapturePage()
			if err != nil {
				finish(err)
				return
			}
			_ = os.MkdirAll(dir, 0755)
			if err := os.WriteFile(filepath.Join(dir, "integrated-native-mini.png"), data, 0600); err != nil {
				finish(err)
				return
			}
			bounds := win.Bounds()
			saved, ok := savedRectangle(store.Get("window-manager-main/aux-window", "trackedBounds"))
			if !ok || saved != bounds {
				finish(fmt.Errorf("native resize was not persisted: saved=%v current=%v", saved, bounds))
				return
			}
			_ = store.Set("window-manager-main/aux-window", "enabled", false)
			win.Close()
			time.Sleep(200 * time.Millisecond)
			if !win.IsDestroyed() {
				finish(fmt.Errorf("disabled Mini did not close"))
				return
			}
			finish(nil)
		}()
	})
	if err := mygo.App.Run(); err != nil {
		return err
	}
	if err := <-result; err != nil {
		return err
	}
	return nil
}
