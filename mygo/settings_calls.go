package main

import (
	"context"
	_ "embed"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"github.com/egoist/mygo"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

//go:embed internal/bridge/assets/default-settings.json
var defaultSettings []byte

func (d *Desktop) settingCall(ctx context.Context, method string, args []any) (any, error) {
	ns, key := textArg(args, 0), textArg(args, 1)
	switch method {
	case "get":
		return d.settingValue(ns, key), nil
	case "set":
		return nil, d.store.Set(ns, key, arg(args, 2))
	case "getByPrefix":
		values := d.settingSnapshot(ns)
		selected := object{}
		for k, v := range values {
			if strings.HasPrefix(k, key) {
				selected[k] = v
			}
		}
		return selected, nil
	case "removeByPrefix":
		return nil, d.store.DeletePrefix(ns, key)
	case "exportSettingsToJsonFile":
		path, err := mygo.Dialog.Save(mygo.SaveDialogOptions{Parent: mygo.CallerWindow(ctx), DefaultPath: "league-akari-mygo-settings.json"})
		if path == "" || err != nil {
			return nil, err
		}
		data, err := json.MarshalIndent(object{"version": 1, "namespaces": d.store.All()}, "", "  ")
		if err != nil {
			return nil, err
		}
		return path, os.WriteFile(path, data, 0600)
	case "importSettingsFromJsonFile":
		paths, err := mygo.Dialog.Open(mygo.OpenDialogOptions{Parent: mygo.CallerWindow(ctx)})
		if len(paths) == 0 || err != nil {
			return nil, err
		}
		if ns != "" {
			return nil, d.store.ImportScoped(paths[0], ns, key)
		}
		return nil, d.store.Import(paths[0])
	}
	return nil, fmt.Errorf("此配置操作尚未迁移：%s", method)
}

func (d *Desktop) settingSnapshot(ns string) object {
	return d.store.Snapshot(ns)
}

func (d *Desktop) settingValue(ns, key string) any { return d.settingSnapshot(ns)[key] }

func (d *Desktop) championConfigCall(method string, args []any) (any, error) {
	if method != "updateRunes" && method != "updateSummonerSpells" {
		return nil, fmt.Errorf("英雄配置操作尚未迁移：%s", method)
	}
	id := int(client.Number(arg(args, 0)))
	if method == "updateRunes" {
		return nil, d.champion.UpdateRunes(id, textArg(args, 1), arg(args, 2))
	}
	return nil, d.champion.UpdateSpells(id, textArg(args, 1), arg(args, 2))
}
