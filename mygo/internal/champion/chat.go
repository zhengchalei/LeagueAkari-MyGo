package champion

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
)

func (r *Runner) gameData() map[string]any {
	if client, ok := r.client.(interface{ State() map[string]any }); ok {
		data, _ := client.State()["gameData"].(map[string]any)
		return data
	}
	return nil
}

func (r *Runner) assetName(kind string, id int) string {
	data := r.gameData()
	entries, _ := data[kind].(map[string]any)
	if kind == "perkstyles" {
		entries, _ = entries["styles"].(map[string]any)
	}
	entry, _ := entries[strconv.Itoa(id)].(map[string]any)
	if name, _ := entry["name"].(string); name != "" {
		return name
	}
	return strconv.Itoa(id)
}

func (r *Runner) english() bool { return r.settings.Get("app-common-main", "locale") == "en" }

func (r *Runner) conversation(ctx context.Context) string {
	if client, ok := r.client.(interface{ State() map[string]any }); ok {
		chat, _ := client.State()["chat"].(map[string]any)
		conversations, _ := chat["conversations"].(map[string]any)
		room, _ := conversations["championSelect"].(map[string]any)
		id, _ := room["id"].(string)
		return id
	}
	var conversations []struct {
		ID   string `json:"id"`
		Type string `json:"type"`
	}
	if r.get(ctx, "/lol-chat/v1/conversations", &conversations) == nil {
		for _, conversation := range conversations {
			if conversation.Type == "championSelect" {
				return conversation.ID
			}
		}
	}
	return ""
}

func (r *Runner) sendChat(ctx context.Context, message string) {
	id := r.conversation(ctx)
	if id == "" || ctx.Err() != nil {
		return
	}
	_, err := r.client.JSON(ctx, http.MethodPost, "/lol-chat/v1/conversations/"+url.PathEscape(id)+"/messages", map[string]any{
		"body": "[League Akari] " + message, "fromPid": "", "fromSummonerId": 0, "id": id,
		"isHistorical": false, "timestamp": "", "type": "celebration",
	})
	if err != nil && ctx.Err() == nil && r.emit != nil {
		r.emit(Namespace, "error-chat-send", map[string]any{"message": err.Error()})
	}
}

func modeKey(self selection) string {
	if self.Mode == "CLASSIC" && strings.HasPrefix(self.QueueType, "RANKED_") {
		return "ranked-" + self.Position
	}
	return map[string]string{"CLASSIC": "normal", "ARAM": "aram", "KIWI": "aram", "URF": "urf", "NEXUSBLITZ": "nexusblitz", "ULTBOOK": "ultbook"}[self.Mode]
}

func (r *Runner) announce(ctx context.Context, self selection, config Config) {
	id := r.conversation(ctx)
	if id == r.conversationKey {
		return
	}
	r.conversationKey = id
	key := modeKey(self)
	if id == "" || key == "" {
		return
	}
	ids := map[int]bool{}
	for hero, runes := range config.Runes {
		if runes[key] != nil || strings.HasPrefix(self.QueueType, "RANKED_") && runes["ranked-default"] != nil {
			id, _ := strconv.Atoi(hero)
			ids[id] = true
		}
	}
	for hero, spells := range config.SummonerSpells {
		if spells[key] != nil || strings.HasPrefix(self.QueueType, "RANKED_") && spells["ranked-default"] != nil {
			id, _ := strconv.Atoi(hero)
			ids[id] = true
		}
	}
	ordered := []int{}
	for id := range ids {
		ordered = append(ordered, id)
	}
	sort.Ints(ordered)
	names := []string{}
	for _, id := range ordered[:min(16, len(ordered))] {
		names = append(names, r.assetName("champions", id))
	}
	message := "已启用自动配置，但无已配置的英雄"
	if r.english() {
		message = "Auto configuration enabled, but no champion has been configured"
	}
	if len(names) > 0 {
		message = "已启用自动配置，已配置的英雄：" + strings.Join(names, ", ")
		if r.english() {
			message = "Auto configuration enabled, configured champions: " + strings.Join(names, ", ")
		}
	}
	r.sendChat(ctx, message)
}

func (r *Runner) sendApplicationMessage(ctx context.Context, self selection, kind string, runes *RunesConfig, spells *SpellsConfig, err error) {
	if ctx.Err() != nil || errors.Is(err, context.Canceled) {
		return
	}
	name := strings.TrimPrefix(r.pageName(self), "[Akari] ")
	message := ""
	if kind == "runes" {
		perks := []string{}
		for _, id := range runes.SelectedPerkIDs {
			perks = append(perks, r.assetName("perks", id))
		}
		message = fmt.Sprintf("%s 的符文配法已更新为 %s / %s (%s)", name, r.assetName("perkstyles", runes.PrimaryStyleID), r.assetName("perkstyles", runes.SubStyleID), strings.Join(perks, ", "))
		if r.english() {
			message = fmt.Sprintf("%s rune setup has been updated to %s / %s (%s)", name, r.assetName("perkstyles", runes.PrimaryStyleID), r.assetName("perkstyles", runes.SubStyleID), strings.Join(perks, ", "))
		}
		if err != nil {
			message = name + " 的符文配法更新失败"
			if r.english() {
				message = "Failed to update rune setup for " + name
			}
		}
	} else {
		message = fmt.Sprintf("%s 的召唤师技能已更新为 [%s] [%s]", name, r.assetName("summonerSpells", spells.Spell1ID), r.assetName("summonerSpells", spells.Spell2ID))
		if r.english() {
			message = fmt.Sprintf("%s summoner spells have been updated to [%s] [%s]", name, r.assetName("summonerSpells", spells.Spell1ID), r.assetName("summonerSpells", spells.Spell2ID))
		}
		if err != nil {
			message = name + " 的召唤师技能更新失败"
			if r.english() {
				message = "Failed to update summoner spells for " + name
			}
		}
	}
	r.sendChat(ctx, message)
}
