package main

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/winuiipc"
	nativemini "github.com/zhengchalei/LeagueAkari-MyGo/mygo/native_mini"
)

// WinUI owns all windows and the pipe server. This process only supplies the
// Go services; it never enters MyGo's App.Run or creates a WebView.
func runWinUIBackend(args []string) (bool, error) {
	if len(args) == 0 || args[0] != "--winui-backend" {
		return false, nil
	}
	if len(args) < 2 {
		return true, errors.New("named pipe argument required")
	}
	name := args[1]
	if !strings.HasPrefix(name, "league-akari-winui-") || strings.ContainsAny(name, `/\:`) {
		return true, errors.New("invalid private pipe name")
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	stream, err := connectWinUIPipe(ctx, name)
	if err != nil {
		return true, err
	}
	session := winuiipc.New(stream)
	dir, explicitUserData, err := resolveWinUIUserData(args)
	if err != nil {
		stream.Close()
		return true, err
	}
	hostPID, hostExecutable := 0, ""
	for i := 2; i+1 < len(args); i += 2 {
		switch args[i] {
		case "--host-pid":
			hostPID, _ = strconv.Atoi(args[i+1])
		case "--host-exe":
			hostExecutable = args[i+1]
		}
	}
	if err = os.MkdirAll(dir, 0700); err != nil {
		return true, err
	}
	if file, err := os.OpenFile(filepath.Join(dir, "league-akari-winui-backend.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600); err == nil {
		defer file.Close()
		log.SetOutput(file)
	}
	d := &Desktop{userData: dir, skipExternalStorageMigration: explicitUserData, hostCall: session.HostCall, eventSink: func(event bridge.Event) {
		if session.Send(winuiipc.Message{Type: "event", Event: event}) != nil {
			cancel()
		}
	}}
	d.winUIHostPID, d.winUIHostExecutable = hostPID, hostExecutable
	if err = d.initializeWithDirectory(ctx, dir); err != nil {
		stream.Close()
		return true, err
	}
	defer d.prepareQuit(dir)
	mini := nativemini.New(d.client.JSON)
	mini.OnChange = func() { d.emit("winui-backend", "miniChanged") }
	if err = session.Send(winuiipc.Message{Type: "ready", Version: appVersion}); err != nil {
		return true, err
	}
	go d.run(ctx)
	err = session.Serve(ctx, func(ctx context.Context, request winuiipc.Message) winuiipc.Result {
		var result CallResult
		if request.Namespace == "winui-backend" {
			data, callErr := d.winUIBackendCall(ctx, mini, request.Method, request.Args)
			result = CallResult{Success: callErr == nil, Data: data}
			if callErr != nil {
				result.Error = object{"message": callErr.Error()}
			}
		} else {
			result = d.Call(ctx, request.Namespace, request.Method, request.Args)
		}
		return winuiipc.Result{Success: result.Success, Data: result.Data, Error: result.Error}
	})
	cancel()
	return true, err
}

func resolveWinUIUserData(args []string) (string, bool, error) {
	for i := 2; i < len(args); i += 2 {
		if args[i] != "--user-data" {
			continue
		}
		if i+1 >= len(args) || strings.TrimSpace(args[i+1]) == "" {
			return "", true, errors.New("user data directory argument required")
		}
		return args[i+1], true, nil
	}
	base, err := os.UserConfigDir()
	if err != nil {
		return "", false, err
	}
	dir, err := prepareUserData(base)
	return dir, false, err
}

func (d *Desktop) winUIBackendCall(ctx context.Context, mini *nativemini.Model, method string, args []any) (any, error) {
	switch method {
	case "publicRequest":
		location := textArg(args, 0)
		parsed, err := url.Parse(location)
		if err != nil || parsed.Scheme != "https" || parsed.Host != "lol-api-champion.op.gg" || !strings.HasPrefix(parsed.Path, "/api/") {
			return nil, errors.New("仅支持 OP.GG 英雄公开接口")
		}
		return d.fetchJSON(ctx, location)
	case "lcuRequest":
		return d.client.JSON(ctx, strings.ToUpper(textArg(args, 0)), textArg(args, 1), arg(args, 2))
	case "riotRequest":
		method, endpoint := strings.ToUpper(textArg(args, 0)), textArg(args, 1)
		parsed, err := url.Parse(endpoint)
		if err != nil || parsed.IsAbs() || parsed.Host != "" || !strings.HasPrefix(parsed.Path, "/player-account/") {
			return nil, errors.New("仅支持本地 Riot 玩家账号接口")
		}
		body, err := json.Marshal(arg(args, 2))
		if err != nil {
			return nil, err
		}
		request, err := http.NewRequestWithContext(ctx, method, "http://local"+endpoint, bytes.NewReader(body))
		if err != nil {
			return nil, err
		}
		response := httptest.NewRecorder()
		d.playerAccountProxy(response, request)
		if response.Code < 200 || response.Code >= 300 {
			return nil, fmt.Errorf("Riot 玩家账号接口返回 %d: %s", response.Code, strings.TrimSpace(response.Body.String()))
		}
		var value any
		err = json.Unmarshal(response.Body.Bytes(), &value)
		return value, err
	case "sgpRequest":
		return d.client.SGPJSON(ctx, textArg(args, 0), textArg(args, 1), strings.ToUpper(textArg(args, 2)), textArg(args, 3), arg(args, 4))
	case "snapshot":
		return d.state(textArg(args, 0), textArg(args, 1)), nil
	case "miniSnapshot":
		view := d.client.State()
		mini.Refresh(ctx, nativemini.Inputs{Client: view, Kiwi: d.state("extra-assets-main", "kiwi"), Fandom: d.state("extra-assets-main", "fandom"), OPGG: d.state("extra-assets-main", "opgg"), ShowSkins: d.settingValue("window-manager-main/aux-window", "showSkinSelector") != false})
		data, _ := json.Marshal(d.winUIMiniControls())
		controls := object{}
		_ = json.Unmarshal(data, &controls)
		controls["DodgeLooping"] = d.winUIDodgeActive.Load()
		controls["DodgeIterations"] = d.winUIDodgeCount.Load()
		return object{"snapshot": mini.Snapshot(), "controls": controls, "state": winUIMiniState(view), "automation": d.automation.State(), "settings": object{"autoGameflow": d.settingSnapshot("auto-gameflow-main"), "autoSelect": d.settingSnapshot("auto-select-main")}}, nil
	case "miniAction":
		var action nativemini.Action
		data, _ := json.Marshal(arg(args, 0))
		if err := json.Unmarshal(data, &action); err != nil {
			return nil, err
		}
		// UI snapshots are polled; validate selection operations against the
		// latest client events rather than the last rendered Mini snapshot.
		switch action.Kind {
		case "champion", "champion-preview", "skin", "reroll", "reroll-grab-back":
			mini.Refresh(ctx, nativemini.Inputs{Client: d.client.State(), Kiwi: d.state("extra-assets-main", "kiwi"), Fandom: d.state("extra-assets-main", "fandom"), OPGG: d.state("extra-assets-main", "opgg"), ShowSkins: d.settingValue("window-manager-main/aux-window", "showSkinSelector") != false})
		}
		switch action.Kind {
		case "set-auto-accept":
			return nil, d.store.Set("auto-gameflow-main", "autoAcceptEnabled", action.Value)
		case "set-auto-matchmaking":
			return nil, d.store.Set("auto-gameflow-main", "autoMatchmakingEnabled", action.Value)
		case "set-wait-invitees":
			return nil, d.store.Set("auto-gameflow-main", "autoMatchmakingWaitForInvitees", action.Value)
		case "set-auto-min-members":
			if action.ID < 1 || action.ID > 99 {
				return nil, errors.New("最少成员数必须为 1 到 99")
			}
			return nil, d.store.Set("auto-gameflow-main", "autoMatchmakingMinimumMembers", action.ID)
		case "set-auto-delay", "set-auto-accept-delay":
			if action.ID < 0 || action.ID > 300000 {
				return nil, errors.New("延迟必须为 0 到 300 秒")
			}
			key := "autoMatchmakingDelaySeconds"
			if action.Kind == "set-auto-accept-delay" {
				key = "autoAcceptDelaySeconds"
			}
			return nil, d.store.Set("auto-gameflow-main", key, float64(action.ID)/1000)
		case "champion-preview":
			return nil, mini.ChooseChampion(ctx, action.ID, false)
		case "start-dodge-loop":
			return nil, d.startWinUIDodgeLoop(ctx)
		case "cancel-dodge-loop":
			d.winUIDodgeMu.Lock()
			if d.winUIDodgeCancel != nil {
				d.winUIDodgeCancel()
			}
			d.winUIDodgeMu.Unlock()
			return nil, nil
		case "champion":
			return nil, mini.ChooseChampion(ctx, action.ID, true)
		case "skin":
			return nil, mini.ChooseSkin(ctx, action.ID)
		case "reroll", "reroll-grab-back":
			return nil, mini.Reroll(ctx, action.Kind == "reroll-grab-back")
		case "pin":
			return nil, d.store.Set("window-manager-main/aux-window", "pinned", action.Value)
		case "disable-auto":
			d.automation.SetTemporarilyDisabled(action.Value)
			return nil, nil
		case "accept", "decline":
			if client.String(client.Map(d.client.State()["gameflow"])["phase"]) == "ReadyCheck" {
				return d.client.JSON(ctx, http.MethodPost, "/lol-matchmaking/v1/ready-check/"+action.Kind, nil)
			}
			return nil, nil
		case "cancel-queue":
			if d.winUIMiniControls().CanCancel {
				value, err := d.client.JSON(ctx, http.MethodDelete, "/lol-lobby/v2/lobby/matchmaking/search", nil)
				if err == nil {
					err = d.store.Set("auto-gameflow-main", "autoMatchmakingEnabled", false)
				}
				return value, err
			}
			return nil, nil
		case "cancel-auto-accept":
			d.automation.CancelAutoAccept()
			return nil, nil
		case "cancel-auto-matchmaking":
			d.automation.CancelAutoMatchmaking()
			return nil, d.store.Set("auto-gameflow-main", "autoMatchmakingEnabled", false)
		case "dodge":
			if !d.winUIMiniControls().CanDodge {
				return nil, nil
			}
			result, err := d.hostCall(ctx, "host-ui", "confirm", []any{"退出英雄选择", "确定退出当前英雄选择？可能产生等待惩罚。"})
			if err != nil {
				return nil, err
			}
			if result != true {
				return nil, nil
			}
			params := url.Values{"destination": {"lcdsServiceProxy"}, "method": {"call"}, "args": {`["", "teambuilder-draft", "quitV2", ""]`}}
			return d.client.JSON(ctx, http.MethodPost, "/lol-login/v1/session/invoke?"+params.Encode(), object{"data": []any{"", "teambuilder-draft", "quitV2", ""}})
		}
	case "image", "proxy":
		path := textArg(args, 0)
		if strings.HasPrefix(path, "/lol-") {
			path = "akari://league-client" + path
		}
		if !strings.HasPrefix(path, "akari://") {
			return nil, errors.New("only local akari protocol paths are supported")
		}
		u, err := url.Parse(path)
		if err != nil {
			return nil, err
		}
		u.Path = "/" + u.Host + u.Path
		u.Host = "local"
		u.Scheme = "http"
		verb := textArg(args, 1)
		if verb == "" {
			verb = http.MethodGet
		}
		body := textArg(args, 2)
		r, err := http.NewRequestWithContext(ctx, verb, u.String(), strings.NewReader(body))
		if err != nil {
			return nil, err
		}
		response := httptest.NewRecorder()
		d.proxy(response, r)
		res := response.Result()
		defer res.Body.Close()
		data, err := io.ReadAll(io.LimitReader(res.Body, (16<<20)+1))
		if err != nil {
			return nil, err
		}
		if len(data) > 16<<20 {
			return nil, errors.New("asset response exceeds 16 MiB")
		}
		if res.StatusCode >= 400 {
			return nil, fmt.Errorf("local asset response %d: %s", res.StatusCode, string(data))
		}
		return object{"mime": res.Header.Get("Content-Type"), "base64": base64.StdEncoding.EncodeToString(data), "status": res.StatusCode}, nil
	}
	return nil, fmt.Errorf("unknown WinUI backend method %s", method)
}

func (d *Desktop) winUIMiniControls() nativemini.Controls {
	view := d.client.State()
	phase := client.String(client.Map(view["gameflow"])["phase"])
	session := client.Map(client.Map(view["champSelect"])["session"])
	auto := d.automation.State()
	selection := client.Map(auto["auto-select-main"])
	flow := client.Map(auto["auto-gameflow-main"])
	control := nativemini.Controls{Theme: client.String(d.settingValue("app-common-main", "theme")), Locale: client.String(d.settingValue("app-common-main", "locale")), Pinned: d.settingValue("window-manager-main/aux-window", "pinned") == true, CanDodge: phase == "ChampSelect" && session["isSpectating"] != true && client.Map(client.Map(client.Map(view["gameflow"])["session"])["gameData"])["isCustomGame"] != true, TemporarilyDisabled: selection["temporarilyDisabled"] == true, CanAccept: phase == "ReadyCheck" && client.Map(client.Map(view["matchmaking"])["readyCheck"])["playerResponse"] == "None", CanCancel: phase == "Matchmaking", CanCancelAutoAccept: flow["willAccept"] == true, CanCancelAutoMatchmaking: flow["willSearchMatch"] == true}
	for _, plan := range []struct{ key, label string }{{"delayedPick", "自动选择"}, {"delayedBan", "自动禁用"}, {"delayedBenchSwap", "自动交换"}, {"delayedChampionSwap", "自动接受交换"}} {
		value := client.Map(selection[plan.key])
		if len(value) > 0 {
			control.Plans = append(control.Plans, fmt.Sprintf("%s · %.1f 秒", plan.label, max(0, float64(client.Number(value["finishAt"])-time.Now().UnixMilli())/1000)))
		}
	}
	if control.CanCancelAutoAccept {
		control.Plans = append(control.Plans, fmt.Sprintf("自动接受对局 · %.1f 秒", max(0, float64(client.Number(flow["willAcceptAt"])-time.Now().UnixMilli())/1000)))
	}
	return control
}

// Mini needs live selection fields and champion names, not the full item/perk asset catalog.
func winUIMiniState(view map[string]any) object {
	result := object{}
	for _, key := range []string{"gameflow", "champSelect", "lobby", "matchmaking"} {
		result[key] = view[key]
	}
	result["gameData"] = object{"champions": client.Map(view["gameData"])["champions"]}
	return result
}
