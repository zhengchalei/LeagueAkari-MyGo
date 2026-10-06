package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/automation"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/champion"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/game"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/misc"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/platform"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/player"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/respawn"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
	selfupdate "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/update"
)

type object = map[string]any

// Desktop implements the existing UI contract with a Go host and one WebView2.
type Desktop struct {
	quitting             atomic.Bool
	mu                   sync.RWMutex
	windowMu             sync.Mutex
	store                *settings.Store
	client               *client.Client
	game                 *game.Service
	automation           *automation.Runner
	champion             *champion.Runner
	player               *player.Service
	platform             *platform.Service
	misc                 *misc.Service
	respawn              *respawn.Service
	updater              *selfupdate.Service
	fixedShortcutTargets map[string]bool
	sendMu               sync.Mutex
	ctx                  context.Context
	externalClient       *http.Client
	proxyRequests        sync.Map
	windows              map[string]*mygo.Window
	static               map[string]object
	started              time.Time
	tray                 *mygo.Tray
}

type CallResult struct {
	Success bool   `json:"success"`
	Data    any    `json:"data"`
	Error   object `json:"error,omitempty"`
}

func (d *Desktop) initialize(ctx context.Context) error {
	dir, err := mygo.App.Path(mygo.PathUserData)
	if err != nil {
		return err
	}
	d.store, err = settings.New(filepath.Join(dir, "settings.json"))
	if err != nil {
		return err
	}
	d.windows = map[string]*mygo.Window{}
	d.static = staticStates()
	d.static["extra-assets-main:opgg"]["cached"] = true
	d.loadKiwiBalance(dir)
	d.started = time.Now()
	d.ctx = ctx
	defaults := map[string]map[string]any{}
	if err = json.Unmarshal(defaultSettings, &defaults); err != nil {
		return err
	}
	d.store.ApplyDefaults(defaults)
	if level := d.store.Get("logger-factory-main", "logLevel"); level != nil {
		d.static["logger-factory-main:state"]["logLevel"] = level
	}
	if !d.store.HasPersisted("ongoing-game-main", "matchHistoryLoadCount") {
		_ = d.store.Set("ongoing-game-main", "matchHistoryLoadCount", 20)
	}
	d.externalClient = newExternalHTTPClient(d.store)
	d.client = client.NewWithOptions(d.clientEvent, client.Options{SGPHTTPClient: &http.Client{
		Transport: d.externalClient.Transport, Timeout: 15 * time.Second,
	}})
	d.client.SetEventHandler(func(uri, eventType string, data any) {
		d.clientEvent("league-client-main", "lcu-event", uri, eventType, data)
	})
	d.client.SetAutoConnect(d.settingValue("league-client-main", "autoConnect") != false)
	d.platform = platform.New(platform.Options{Client: d.client, Store: d.store, Emit: d.emit, WindowAction: d.windowCallByName})
	d.static["app-common-main:state"]["nativeSupport"] = d.platform.NativeSupport()
	d.static["app-common-main:state"]["isElevated"] = d.platform.IsElevated()
	d.player, err = player.New(filepath.Join(dir, "players.sqlite"), d.store, d.emit)
	if err != nil {
		return err
	}
	d.player.SetFileDialog(func(kind string) (string, error) {
		if kind == "save" {
			return mygo.Dialog.Save(mygo.SaveDialogOptions{DefaultPath: "league-akari-mygo-player-tags.json"})
		}
		paths, err := mygo.Dialog.Open(mygo.OpenDialogOptions{})
		if len(paths) == 0 {
			return "", err
		}
		return paths[0], err
	})
	d.game = game.New(d.client, d.emit)
	d.game.SetSettings(d.store)
	d.game.SetPlayerStore(d.player)
	d.automation = automation.New(d.client, d.store, d.emit)
	d.misc = misc.New(d.client, d.store, d.emit)
	d.respawn = respawn.New(d.client, d.platform, d.store, d.emit)
	d.updater = selfupdate.New(selfupdate.Options{Directory: filepath.Join(dir, "new-updates"), Version: appVersion, Repository: updateRepository(), HTTP: &http.Client{Transport: d.externalClient.Transport, Timeout: 30 * time.Minute}, Emit: d.emit})
	d.fixedShortcutTargets = map[string]bool{}
	d.syncSendShortcuts()
	d.champion = champion.New(d.client, d.store, d.emit)
	var groups []automation.SelectGroup
	_ = json.Unmarshal(selectGroups, &groups)
	d.automation.SetSelectGroups(groups)
	if d.store.Get("mygo-main", "memorySettingsMigrated") != true {
		if err := d.store.Set("ongoing-game-main", "matchHistoryLoadCount", 20); err != nil {
			return err
		}
		if err := d.store.Set("ongoing-game-main", "gameDetailsLoadCount", 0); err != nil {
			return err
		}
		if err := d.store.Set("mygo-main", "memorySettingsMigrated", true); err != nil {
			return err
		}
	}
	d.game.SetMatchHistoryLoadCount(int(client.Number(d.settingValue("ongoing-game-main", "matchHistoryLoadCount"))))
	d.store.OnChange(d.settingChanged)
	return nil
}

func (d *Desktop) clientEvent(namespace, name string, args ...any) {
	if namespace == "league-client-main" && name == "lcu-event" && len(args) == 3 {
		uri, eventType := client.String(args[0]), client.String(args[1])
		ctx := d.ctx
		if ctx == nil {
			ctx = context.Background()
		}
		if d.misc != nil {
			_ = d.misc.HandleLCUEvent(ctx, uri, eventType, args[2])
		}
		if d.automation != nil {
			_ = d.automation.HandleLCUEvent(ctx, uri, eventType, args[2])
		}
		state := d.state("renderer-debug-main", "state")
		if state["logAllLcuEvents"] == true {
			log.Printf("LCU %s %s", eventType, uri)
		}
		if state["sendAllNativeLcuEvents"] == true {
			d.emit("renderer-debug-main", "lc-event", object{"uri": uri, "eventType": eventType, "data": args[2]})
		}
		return
	}
	d.emit(namespace, name, args...)
}

func (d *Desktop) emit(namespace, name string, args ...any) {
	rendererEvents.Broadcast(bridge.Event{Namespace: namespace, Name: name, Args: args})
}

func (d *Desktop) update(namespace, state, key string, value any) {
	d.emit("mobx-utils-main", "update-state-prop/"+namespace+":"+state, key, value, object{"action": "update", "raw": true})
}

func (d *Desktop) run(ctx context.Context) {
	go d.client.Poll(ctx)
	go d.automation.Run(ctx)
	go d.champion.Run(ctx)
	go d.platform.Run(ctx)
	go d.misc.Run(ctx)
	go d.respawn.Run(ctx)
	go d.refreshExtraAssets(ctx)
	go d.migrateStorage()
	go d.refreshBalance(ctx)
	go d.refreshKiwiBalance(ctx)
	ticker := time.NewTicker(3 * time.Second)
	defer ticker.Stop()
	previousSGP := object{}
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			d.game.Refresh(ctx)
			currentSGP := d.sgpState()
			for k, v := range currentSGP {
				if !reflect.DeepEqual(previousSGP[k], v) {
					d.update("sgp-main", "state", k, v)
				}
			}
			previousSGP = currentSGP
		}
	}
}

// Call preserves namespaced renderer calls without loading Node or Electron.
func (d *Desktop) Call(ctx context.Context, namespace, method string, args []any) (result CallResult) {
	defer func() {
		if p := recover(); p != nil {
			log.Printf("IPC %s.%s: %v", namespace, method, p)
			result = CallResult{Error: object{"name": "Error", "message": "操作失败，请重试"}}
		}
	}()
	data, err := d.dispatch(ctx, namespace, method, args)
	if err != nil {
		return CallResult{Error: object{"name": "Error", "message": err.Error()}}
	}
	return CallResult{Success: true, Data: data}
}

func (d *Desktop) dispatch(ctx context.Context, ns, method string, args []any) (any, error) {
	switch ns {
	case "mobx-utils-main":
		if method == "subscribeAndGetInitialState" {
			values := d.state(textArg(args, 0), textArg(args, 1))
			wrapped := object{}
			for key, value := range values {
				wrapped[key] = object{"value": value, "config": object{"raw": true}}
			}
			return wrapped, nil
		}
	case "setting-factory-main":
		return d.settingCall(ctx, method, args)
	case "app-common-main":
		switch method {
		case "getVersion":
			return appVersion, nil
		case "getRuntimeInfo":
			return d.runtimeInfo(), nil
		case "readClipboardText":
			return mygo.Clipboard.ReadText(), nil
		case "exit":
			mygo.App.Quit()
			return nil, nil
		case "openUserDataDir":
			dir, _ := mygo.App.Path(mygo.PathUserData)
			return nil, mygo.Shell.OpenPath(dir)
		case "setDisableHardwareAcceleration":
			value, err := d.platform.Call(ctx, ns, method, args)
			if err != nil {
				return nil, err
			}
			if d.updater.Prepared() == "" {
				executable, err := os.Executable()
				if err != nil {
					return nil, err
				}
				command := exec.Command(executable, "--league-akari-mygo-relaunch")
				command.Dir = filepath.Dir(executable)
				if err = command.Start(); err != nil {
					return nil, err
				}
			}
			mygo.App.Quit()
			return value, nil
		}
	case "logger-factory-main":
		switch method {
		case "log":
			levels := map[string]int{"debug": 0, "info": 1, "warn": 2, "error": 3, "silent": 4}
			threshold := client.String(d.state(ns, "state")["logLevel"])
			if threshold == "" {
				threshold = "info"
			}
			if levels[textArg(args, 1)] >= levels[threshold] {
				log.Printf("Renderer %s %s", textArg(args, 0), textArg(args, 2))
			}
			return nil, nil
		case "setLogLevel":
			err := d.store.Set(ns, "logLevel", arg(args, 0))
			if err == nil {
				d.setStatic(ns+":state", "logLevel", arg(args, 0))
				d.update(ns, "state", "logLevel", arg(args, 0))
			}
			return nil, err
		case "openLogsDir":
			dir, _ := mygo.App.Path(mygo.PathUserData)
			return nil, mygo.Shell.OpenPath(dir)
		}
	case "league-client-main":
		switch method {
		case "subscribeLcuEndpoint":
			return d.client.Subscribe(textArg(args, 0)), nil
		case "unsubscribeLcuEndpoint":
			return d.client.Unsubscribe(textArg(args, 0)), nil
		case "connect":
			auth, err := d.platform.ResolveAuth(ctx, arg(args, 0))
			if err != nil {
				return nil, err
			}
			return nil, d.client.Connect(ctx, auth)
		case "disconnect":
			d.client.Disconnect()
			return nil, nil
		case "http-request":
			cfg := asObject(arg(args, 0))
			method := strings.ToUpper(client.String(cfg["method"]))
			if method == "" {
				method = "GET"
			}
			data, err := d.client.JSON(ctx, method, client.String(cfg["url"]), cfg["data"])
			return object{"data": data, "status": 200, "statusText": "OK", "headers": object{}, "config": cfg}, err
		case "writeItemSetsToDisk", "fixWindowMethodA", "peekClient":
			return d.platform.Call(ctx, ns, method, args)
		}
	case "ongoing-game-main":
		switch method {
		case "getAll":
			return d.game.GetAll(), nil
		case "loadGameDetails":
			return d.game.GetTimeline(ctx, textArg(args, 1), client.Number(arg(args, 0)))
		case "reload":
			return nil, d.game.Reload(ctx)
		case "reloadPlayer":
			return nil, d.game.ReloadPlayerWithOptions(ctx, textArg(args, 0), asObject(arg(args, 1)))
		case "setMatchHistoryTagParams":
			d.game.SetMatchHistoryTagParams(asObject(arg(args, 0)))
			d.game.Refresh(ctx)
			return nil, nil
		case "setDraft":
			return nil, d.game.SetDraft(asObject(arg(args, 0)))
		case "clearDraft":
			d.game.ClearDraft()
			return nil, nil
		}
	case "auto-gameflow-main":
		switch method {
		case "cancelAutoAccept":
			d.automation.CancelAutoAccept()
			return nil, nil
		case "cancelAutoMatchmaking":
			d.automation.CancelAutoMatchmaking()
			return nil, nil
		case "setFriendsToBeInvited":
			friends := []string{}
			for _, entry := range client.List(arg(args, 0)) {
				friends = append(friends, client.String(entry))
			}
			d.automation.SetFriendsToBeInvited(friends)
			return nil, nil
		}
	case "auto-select-main":
		switch method {
		case "setTemporarilyDisabled":
			v, _ := arg(args, 0).(bool)
			d.automation.SetTemporarilyDisabled(v)
			return nil, nil
		case "setPickConfig", "setBanConfig":
			key := "pickConfig"
			if method == "setBanConfig" {
				key = "banConfig"
			}
			configs := asObject(d.store.Get(ns, key))
			group := textArg(args, 0)
			cfg := asObject(configs[group])
			mergeObject(cfg, asObject(arg(args, 1)))
			configs[group] = cfg
			return nil, d.store.Set(ns, key, configs)
		}
	case "saved-player-main":
		return d.player.Call(method, args)
	case "auto-champ-config-main":
		return d.championConfigCall(method, args)
	case "akari-protocol-main":
		if method == "cancelProxyRequest" {
			if cancel, ok := d.proxyRequests.Load(textArg(args, 0)); ok {
				cancel.(context.CancelFunc)()
				return true, nil
			}
			return false, nil
		}
	case "renderer-debug-main":
		if method == "setLogAllLcuEvents" || method == "setSendAllNativeLcuEvents" {
			key := "logAllLcuEvents"
			if method == "setSendAllNativeLcuEvents" {
				key = "sendAllNativeLcuEvents"
			}
			d.setStatic(ns+":state", key, arg(args, 0))
			d.update(ns, "state", key, arg(args, 0))
			return nil, nil
		}
	case "akari-api-main":
		return d.apiCall(ctx, method, args)
	case "in-game-send-main":
		return d.sendCall(ctx, method, args)
	case "auto-misc-main":
		return d.misc.Call(ctx, method, args)
	case "self-update-main":
		return d.updateCall(ctx, method, args)
	case "remote-config-main":
		return d.remoteCall(ctx, method, args)
	case "client-installation-main", "league-client-ux-main", "keyboard-shortcuts-main", "game-client-main":
		return d.platform.Call(ctx, ns, method, args)
	}
	if strings.HasPrefix(ns, "window-manager-main/") {
		return d.windowCall(ns, method, args)
	}
	if ns == "app-common-main" {
		return d.platform.Call(ctx, ns, method, args)
	}
	return nil, fmt.Errorf("未知操作：%s.%s", ns, method)
}

func (d *Desktop) state(ns, id string) object {
	if id == "settings" {
		return d.settingSnapshot(ns)
	}
	if ns == "league-client-main" {
		return asObject(d.client.State()[id])
	}
	if ns == "ongoing-game-main" && id == "state" {
		return d.game.State()
	}
	if ns == "sgp-main" {
		return d.sgpState()
	}
	if ns == "auto-gameflow-main" || ns == "auto-select-main" {
		return asObject(d.automation.State()[ns])
	}
	if ns == "auto-champ-config-main" {
		return d.champion.State()
	}
	if ns == "self-update-main" {
		return d.updater.State()
	}
	if ns == "remote-config-main" && id == "state" {
		d.mu.RLock()
		value := asObject(client.Clone(d.static[ns+":"+id]))
		d.mu.RUnlock()
		if latest := d.updater.Latest(); latest != nil {
			value["latestRelease"] = latest
		}
		return value
	}
	if ns == "respawn-timer-main" {
		return asObject(d.respawn.State()[ns])
	}
	if ns == "auto-misc-main" {
		return asObject(d.misc.State()[ns])
	}
	if ns == "client-installation-main" || ns == "league-client-ux-main" || ns == "keyboard-shortcuts-main" {
		return d.platform.State(ns)
	}
	if id == "state" && d.platform != nil && (ns == "window-manager-main" || ns == "window-manager-main/cd-timer-window" || ns == "app-common-main") {
		d.mu.RLock()
		value := asObject(client.Clone(d.static[ns+":"+id]))
		d.mu.RUnlock()
		mergeObject(value, d.platform.State(ns))
		if ns == "app-common-main" {
			value["nativeSupport"] = d.platform.NativeSupport()
			value["isElevated"] = d.platform.IsElevated()
			value["baseConfig"] = object{"disableHardwareAcceleration": d.settingValue("app-common-main", "disableHardwareAcceleration") == true}
		}
		return value
	}
	d.mu.RLock()
	defer d.mu.RUnlock()
	if value := d.static[ns+":"+id]; value != nil {
		return asObject(client.Clone(value))
	}
	return object{}
}

func (d *Desktop) setStatic(id, key string, value any) {
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.static[id] == nil {
		d.static[id] = object{}
	}
	d.static[id][key] = value
}

func (d *Desktop) sgpState() object {
	state := d.client.SGPState()
	d.mu.RLock()
	config := d.static["remote-config-main:state"]
	queues := asObject(config["supportedQueues"])["queues"]
	d.mu.RUnlock()
	if queues != nil {
		state["supportedQueues"] = queues
	}
	return state
}

func arg(args []any, index int) any {
	if index >= len(args) {
		return nil
	}
	return args[index]
}
func textArg(args []any, index int) string { v, _ := arg(args, index).(string); return v }
func asObject(v any) object {
	if m, ok := v.(map[string]any); ok && m != nil {
		return m
	}
	return object{}
}
func lowerFirst(v string) string {
	if v == "" {
		return v
	}
	return strings.ToLower(v[:1]) + v[1:]
}
func mergeObject(dst, src object) {
	for k, v := range src {
		if o, ok := v.(map[string]any); ok {
			child := asObject(dst[k])
			mergeObject(child, o)
			dst[k] = child
		} else {
			dst[k] = v
		}
	}
}
func jsonNormalize(value any) any {
	b, _ := json.Marshal(value)
	var normalized any
	_ = json.Unmarshal(b, &normalized)
	return normalized
}
