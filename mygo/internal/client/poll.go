package client

import (
	"context"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

func (c *Client) Poll(ctx context.Context) {
	go c.eventLoop(ctx)
	_ = c.PollOnce(ctx)
	ticker := time.NewTicker(c.options.PollInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			_ = c.PollOnce(ctx)
		}
	}
}

func (c *Client) PollOnce(ctx context.Context) error {
	c.mu.RLock()
	auth := c.auth
	disabled := c.manualDisconnect || !c.autoConnect
	c.mu.RUnlock()
	if auth == nil {
		if disabled {
			return nil
		}
		found, err := c.options.Discover(ctx)
		if err != nil {
			return err
		}
		if found == nil {
			c.disconnect()
			return nil
		}
		c.SetAuth(found)
		auth = found
	}
	me, err := c.JSON(ctx, http.MethodGet, "/lol-summoner/v1/current-summoner", nil)
	if err != nil {
		c.disconnect()
		return err
	}
	if auth.Region == "" || auth.PlatformID == "" {
		if locale, err := c.JSON(ctx, http.MethodGet, "/riotclient/region-locale", nil); err == nil {
			copied := *auth
			if copied.Region == "" {
				copied.Region = String(Map(locale)["region"])
			}
			if login, err := c.JSON(ctx, http.MethodGet, "/lol-login/v1/session", nil); err == nil {
				copied.PlatformID = String(Map(login)["platformId"])
			}
			c.SetAuth(&copied)
			auth = &copied
		}
	}
	c.set("state", "connectionState", "connected")
	c.set("state", "auth", auth.Public())
	c.set("summoner", "me", me)
	type endpoint struct{ state, key, path string }
	endpoints := []endpoint{
		{"gameflow", "phase", "/lol-gameflow/v1/gameflow-phase"}, {"gameflow", "session", "/lol-gameflow/v1/session"},
		{"lobby", "lobby", "/lol-lobby/v2/lobby"}, {"matchmaking", "readyCheck", "/lol-matchmaking/v1/ready-check"},
		{"matchmaking", "search", "/lol-matchmaking/v1/search"}, {"champSelect", "session", "/lol-champ-select/v1/session"},
	}
	var wg sync.WaitGroup
	for _, item := range endpoints {
		wg.Add(1)
		go func(item endpoint) {
			defer wg.Done()
			value, err := c.JSON(ctx, http.MethodGet, item.path, nil)
			if err != nil {
				value = nil
			}
			c.set(item.state, item.key, value)
		}(item)
	}
	wg.Wait()
	state := c.GameplayState()
	session := Map(Map(state["champSelect"])["session"])
	champion := int64(0)
	for _, member := range List(session["myTeam"]) {
		row := Map(member)
		if Number(row["cellId"]) == Number(session["localPlayerCellId"]) {
			champion = Number(row["championId"])
			break
		}
	}
	if champion > 0 {
		c.set("champSelect", "currentChampion", champion)
	} else {
		c.set("champSelect", "currentChampion", nil)
	}
	if len(session) > 0 {
		for _, item := range []endpoint{{"champSelect", "currentPickableChampionIds", "/lol-champ-select/v1/pickable-champion-ids"}, {"champSelect", "currentBannableChampionIds", "/lol-champ-select/v1/bannable-champion-ids"}, {"champSelect", "disabledChampionIds", "/lol-champ-select/v1/disabled-champion-ids"}, {"champSelect", "skinSelectorInfo", "/lol-champ-select/v1/skin-selector-info"}} {
			if value, err := c.JSON(ctx, http.MethodGet, item.path, nil); err == nil {
				c.set(item.state, item.key, value)
			}
		}
	}
	c.mu.RLock()
	loaded := c.assetsLoaded
	c.mu.RUnlock()
	if !loaded {
		c.loadAssets(ctx)
	}
	_ = c.RefreshTokens(ctx)
	return nil
}

func (c *Client) disconnect() {
	c.SetAuth(nil)
	defaults := initialState()
	for _, sub := range []string{"state", "gameflow", "champSelect", "summoner", "lobby", "matchmaking"} {
		for key, value := range defaults[sub].(map[string]any) {
			c.set(sub, key, value)
		}
	}
}

func (c *Client) loadAssets(ctx context.Context) {
	paths := map[string]string{"champions": "champion-summary.json", "queues": "queues.json", "maps": "maps.json", "items": "items.json", "perks": "perks.json", "summonerSpells": "summoner-spells.json", "gameModeMutators": "game-mode-mutators.json", "augments": "cherry-augments.json"}
	var wg sync.WaitGroup
	gate := make(chan struct{}, 3)
	for key, name := range paths {
		wg.Add(1)
		go func(key, name string) {
			defer wg.Done()
			select {
			case gate <- struct{}{}:
			case <-ctx.Done():
				return
			}
			defer func() { <-gate }()
			data, err := c.JSON(ctx, http.MethodGet, "/lol-game-data/assets/v1/"+name, nil)
			if err != nil {
				return
			}
			entries := map[string]any{}
			for _, value := range List(data) {
				id := Number(Map(value)["id"])
				if key == "gameModeMutators" {
					id = Number(Map(value)["MapId"])
				}
				if id > 0 {
					entries[strconv.FormatInt(id, 10)] = value
				}
			}
			c.set("gameData", key, entries)
		}(key, name)
	}
	wg.Wait()
	if data, err := c.JSON(ctx, http.MethodGet, "/lol-game-data/assets/v1/perkstyles.json", nil); err == nil {
		styles := Map(data)
		indexed := map[string]any{}
		for _, value := range List(styles["styles"]) {
			id := Number(Map(value)["id"])
			indexed[strconv.FormatInt(id, 10)] = value
		}
		styles["styles"] = indexed
		c.set("gameData", "perkstyles", styles)
	}
	c.mu.Lock()
	c.assetsLoaded = true
	c.mu.Unlock()
}

// RegionConfig exposes endpoint configuration and readiness, never the bearer tokens.
func (c *Client) RegionConfig() map[string]any {
	serverID := c.CurrentServer()
	server, ok := c.options.Servers[serverID]
	c.mu.RLock()
	auth := map[string]any{}
	if c.auth != nil {
		auth = c.auth.Public()
	}
	c.mu.RUnlock()
	return map[string]any{"leagueServers": map[string]any{"version": 2, "servers": c.Servers()}, "isTokenReady": c.TokenReady(), "isEntitlementsTokenSet": c.TokenReady(), "isLeagueSessionTokenSet": c.TokenReady(), "supportedQueues": []int{400, 420, 430, 440, 450, 700, 1700, 2400, 2401, 2403, 2405, 2410, 2450}, "availability": map[string]any{"sgpServerId": serverID, "region": auth["region"], "rsoPlatform": auth["rsoPlatformId"], "serversSupported": map[string]any{"matchHistory": ok && server.MatchHistory != "", "common": ok && server.Common != ""}}, "connectionSuccessesCounted": 0, "connectionFailuresCounted": 0}
}

func (c *Client) SGPState() map[string]any { return c.RegionConfig() }

func IsInGame(phase string) bool {
	return strings.Contains("|GameStart|InProgress|Reconnect|WaitingForStats|PreEndOfGame|EndOfGame|", "|"+phase+"|")
}
