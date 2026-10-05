package main

import (
	"context"
	"embed"
	"encoding/json"
	"io/fs"
	"log"
	"os"
	"path/filepath"
	"runtime/debug"
	"syscall"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	selfupdate "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/update"
)

const appVersion = "0.5.0"

//go:embed all:frontend/dist
var frontend embed.FS

var rendererEvents = mygo.NewEvent[bridge.Event]("akari-event")

func main() {
	if handled, err := selfupdate.RunHelper(os.Args[1:]); handled {
		if err != nil {
			log.Print(err)
		}
		return
	}
	// Reclaim transient SGP response buffers promptly instead of growing with history pages.
	debug.SetMemoryLimit(160 << 20)
	mygo.App.SetName(appName)
	mygo.App.SetVersion(appVersion)
	configDir, err := os.UserConfigDir()
	if err != nil {
		log.Fatal(err)
	}
	userData, err := prepareUserData(configDir)
	if err != nil {
		log.Fatal(err)
	}
	if logFile, err := os.OpenFile(filepath.Join(userData, "league-akari-mygo.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600); err == nil {
		defer logFile.Close()
		log.SetOutput(logFile)
	}
	mygo.App.SetPath(mygo.PathUserData, userData)
	if data, err := os.ReadFile(filepath.Join(userData, "settings.json")); err == nil {
		var saved struct {
			Namespaces map[string]map[string]any `json:"namespaces"`
		}
		if json.Unmarshal(data, &saved) == nil && saved.Namespaces["app-common-main"]["disableHardwareAcceleration"] == true {
			existing := os.Getenv("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")
			os.Setenv("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", existing+" --disable-gpu")
		}
	}
	instance, first, err := acquireInstance()
	if err != nil {
		log.Fatal(err)
	}
	if !first {
		return
	}
	defer syscall.CloseHandle(instance)
	web, err := fs.Sub(frontend, "frontend/dist")
	if err != nil {
		log.Fatal(err)
	}
	mygo.SetFrontend(web)
	if !mygo.App.RequestSingleInstanceLock() {
		return
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	desktop := &Desktop{}
	mygo.App.OnSecondInstance(func([]string, string) { desktop.openWindow("main-window") })
	mygo.Bind(desktop)
	mygo.App.WhenReady(func() {
		if err := desktop.initialize(ctx); err != nil {
			log.Printf("Startup failed: %v", err)
			mygo.App.Quit()
			return
		}
		mygo.Protocol.HandleFunc("akari", desktop.proxy)
		desktop.openWindow("main-window")
		desktop.initializeTray()
		go desktop.run(ctx)
	})
	mygo.App.OnBeforeQuit(func(_ *mygo.QuitEvent) {
		desktop.quitting.Store(true)
		cancel()
		desktop.prepareQuit(userData)
	})
	mygo.App.OnWindowAllClosed(func() { mygo.App.Quit() })
	if err := mygo.App.Run(); err != nil {
		log.Fatal(err)
	}
}
