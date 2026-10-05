package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"net/http"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/platform"
)

func (d *Desktop) sendCall(ctx context.Context, method string, args []any) (any, error) {
	const ns = "in-game-send-main"
	switch method {
	case "sendLines":
		lines := []string{}
		for _, entry := range client.List(arg(args, 0)) {
			if line, ok := entry.(string); ok && strings.TrimSpace(line) != "" {
				lines = append(lines, line)
			}
		}
		if len(lines) == 0 {
			return false, nil
		}
		phase := client.String(asObject(d.game.State()["queryStage"])["phase"])
		if phase == "champ-select" || phase == "lobby" {
			chat := asObject(d.client.State()["chat"])
			conversations := asObject(chat["conversations"])
			kind := "championSelect"
			if phase == "lobby" {
				kind = "customGame"
			}
			id := client.String(asObject(conversations[kind])["id"])
			if id == "" {
				return false, nil
			}
			_, err := d.client.JSON(ctx, http.MethodPost, "/lol-chat/v1/conversations/"+id+"/messages", object{"body": strings.Join(lines, "\n"), "type": "chat"})
			return err == nil, err
		}
		if phase != "in-game" {
			return false, nil
		}
		err := d.platform.SendLines(ctx, lines)
		return err == nil, err
	case "sendFixedTextPreset":
		for _, entry := range client.List(d.settingValue(ns, "fixedTextPresetItems")) {
			item := asObject(entry)
			if item["id"] == arg(args, 0) {
				lines := []any{}
				for _, line := range strings.Split(client.String(item["content"]), "\n") {
					lines = append(lines, line)
				}
				return d.sendCall(ctx, "sendLines", []any{lines})
			}
		}
		return false, nil
	case "setRatingPuuids", "setJunglePuuids", "setPremadeIndices":
		key := lowerFirst(strings.TrimPrefix(method, "set"))
		values := arg(args, 0)
		d.setStatic(ns+":state", key, values)
		d.update(ns, "state", key, values)
		return nil, nil
	case "clearPresetSelections":
		for _, key := range []string{"ratingPuuids", "junglePuuids", "premadeIndices"} {
			d.setStatic(ns+":state", key, []any{})
			d.update(ns, "state", key, []any{})
		}
		return nil, nil
	case "setRatingPresetOptions", "setJunglePresetOptions", "setPremadePresetOptions", "updateRatingPresetOptions", "updateJunglePresetOptions", "updatePremadePresetOptions":
		prefix := "set"
		if strings.HasPrefix(method, "update") {
			prefix = "update"
		}
		key := lowerFirst(strings.TrimPrefix(method, prefix))
		value := asObject(arg(args, 0))
		if prefix == "update" {
			value = asObject(d.settingValue(ns, key))
			mergeObject(value, asObject(arg(args, 0)))
		}
		return nil, d.store.Set(ns, key, value)
	case "createFixedTextPresetItem", "updateFixedTextPresetItem", "deleteFixedTextPresetItem", "moveFixedTextPresetItem":
		items := client.List(d.settingValue(ns, "fixedTextPresetItems"))
		index := -1
		for i, entry := range items {
			if asObject(entry)["id"] == arg(args, 0) {
				index = i
				break
			}
		}
		var result any
		switch method {
		case "createFixedTextPresetItem":
			if len(items) >= 100 {
				return nil, errors.New("固定文本最多 100 条")
			}
			id := make([]byte, 16)
			if _, err := rand.Read(id); err != nil {
				return nil, err
			}
			result = object{"id": hex.EncodeToString(id), "title": "", "shortcut": nil, "content": ""}
			items = append(items, result)
		case "updateFixedTextPresetItem":
			if index < 0 {
				return nil, errors.New("未找到固定文本")
			}
			item := asObject(items[index])
			patch := asObject(arg(args, 1))
			for _, key := range []string{"title", "shortcut", "content"} {
				if value, ok := patch[key]; ok {
					item[key] = value
				}
			}
			if len(client.String(item["title"])) > 256 || len(client.String(item["content"])) > 65536 {
				return nil, errors.New("文本超出长度限制")
			}
			result = item
		case "deleteFixedTextPresetItem":
			if index < 0 {
				return false, nil
			}
			items = append(items[:index], items[index+1:]...)
			result = true
		case "moveFixedTextPresetItem":
			target := index - 1
			if textArg(args, 1) == "down" {
				target = index + 1
			}
			if index < 0 || target < 0 || target >= len(items) {
				return false, nil
			}
			items[index], items[target] = items[target], items[index]
			result = true
		}
		return result, d.store.Set(ns, "fixedTextPresetItems", items)
	}
	return nil, errors.New("未知消息操作：" + method)
}

func (d *Desktop) syncSendShortcuts() {
	const ns = "in-game-send-main"
	register := func(id string, value any, callback func(platform.ShortcutDetails)) {
		shortcut := client.String(value)
		if shortcut == "" {
			d.platform.UnregisterTarget(id)
			return
		}
		if err := d.platform.RegisterTarget(id, shortcut, "normal", callback); err != nil {
			d.emit(ns, "shortcut-error", id, err.Error())
		}
	}
	register(ns+"/cancel", d.settingValue(ns, "cancelShortcut"), func(platform.ShortcutDetails) { d.platform.CancelSend() })
	for _, kind := range []string{"Rating", "Jungle", "Premade"} {
		options := asObject(d.settingValue(ns, lowerFirst(kind)+"PresetOptions"))
		for _, target := range []string{"friendly", "enemy", "all"} {
			kind, target := kind, target
			register(ns+"/preset/"+lowerFirst(kind)+"/"+target, asObject(options["targetShortcuts"])[target], func(platform.ShortcutDetails) { d.emit(ns, "request-preset", kind, target) })
		}
	}
	current := map[string]bool{}
	for _, entry := range client.List(d.settingValue(ns, "fixedTextPresetItems")) {
		item := asObject(entry)
		id := client.String(item["id"])
		target := ns + "/preset/fixed-text/" + id
		current[target] = true
		register(target, item["shortcut"], func(platform.ShortcutDetails) {
			_, _ = d.sendCall(context.Background(), "sendFixedTextPreset", []any{id})
		})
	}
	for target := range d.fixedShortcutTargets {
		if !current[target] {
			d.platform.UnregisterTarget(target)
		}
	}
	d.fixedShortcutTargets = current
}
