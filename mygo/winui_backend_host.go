package main

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	selfupdate "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/update"
)

func (d *Desktop) userDataDirectory() (string, error) {
	if d.userData != "" {
		return d.userData, nil
	}
	return mygo.App.Path(mygo.PathUserData)
}
func (d *Desktop) hostWindowCall(name, method string, args []any) (any, error) {
	if d.hostCall != nil {
		return d.hostCall(d.ctx, "window-manager-main/"+name, method, args)
	}
	return d.windowCallByName(name, method, args)
}
func (d *Desktop) headlessCall(ctx context.Context, ns, method string, args []any) (any, bool, error) {
	host := func(ns, method string, args []any) (any, bool, error) {
		value, err := d.hostCall(ctx, ns, method, args)
		return value, true, err
	}
	if strings.HasPrefix(ns, "window-manager-main/") {
		if ns == "window-manager-main/cd-timer-window" && method == "sendInGame" {
			value, err := d.platform.Call(ctx, ns, method, args)
			return value, true, err
		}
		return host(ns, method, args)
	}
	if ns == "app-common-main" {
		switch method {
		case "readClipboardText":
			return host("host-ui", "readClipboardText", nil)
		case "exit":
			return host("host-ui", "exit", nil)
		case "openUserDataDir":
			return host("host-ui", "openPath", []any{d.userData})
		case "setDisableHardwareAcceleration":
			value, err := d.platform.Call(ctx, ns, method, args)
			return value, true, err
		case "relaunchAsAdministrator":
			return host("host-ui", "relaunchAsAdministrator", nil)
		case "getRuntimeInfo":
			info := d.runtimeInfo()
			info["type"] = "winui-backend"
			versions := asObject(info["versions"])
			versions["chrome"] = ""
			info["versions"] = versions
			return info, true, nil
		}
	}
	if ns == "logger-factory-main" && method == "openLogsDir" {
		return host("host-ui", "openPath", []any{d.userData})
	}
	if ns == "self-update-main" {
		switch method {
		case "openNewUpdatesDir":
			path := filepath.Join(d.userData, "new-updates")
			if err := os.MkdirAll(path, 0700); err != nil {
				return nil, true, err
			}
			return host("host-ui", "openPath", []any{path})
		case "uninstallApp":
			// The native Settings page already confirms the destructive action.
			if err := selfupdate.LaunchWinUIUninstall(d.winUIHostExecutable, d.winUIHostPID, d.userData, true); err != nil {
				return nil, true, err
			}
			d.updater.Cancel()
			return host("host-ui", "exit", nil)
		}
	}
	if ns == "setting-factory-main" && (method == "exportSettingsToJsonFile" || method == "importSettingsFromJsonFile") {
		kind := "open"
		if method == "exportSettingsToJsonFile" {
			kind = "save"
		}
		value, err := d.hostCall(ctx, "host-ui", "fileDialog", []any{kind, "league-akari-mygo-settings.json"})
		path := client.String(value)
		if err != nil || path == "" {
			return nil, true, err
		}
		if kind == "save" {
			data, err := json.MarshalIndent(object{"version": 1, "namespaces": d.store.All()}, "", "  ")
			if err != nil {
				return nil, true, err
			}
			return path, true, os.WriteFile(path, data, 0600)
		}
		if ns := textArg(args, 0); ns != "" {
			return path, true, d.store.ImportScoped(path, ns, textArg(args, 1))
		}
		return path, true, d.store.Import(path)
	}
	return nil, false, nil
}
