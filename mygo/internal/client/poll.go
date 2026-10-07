package client

import (
	"context"
	"errors"
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
	parent := ctx
	ctx, cancel := c.scopeConnection(ctx)
	defer cancel()
	c.mu.RLock()
	if !c.connectionCurrentLocked(ctx) {
		c.mu.RUnlock()
		return context.Canceled
	}
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
			c.disconnectForConnection(ctx, false)
			return nil
		}
		bound, release, err := c.discoveredConnection(ctx, parent, found)
		if err != nil {
			return err
		}
		defer release()
		ctx = bound
		auth = found
	}
	var me, loginQueue any
	var summonerErr, queueErr error
	var initial sync.WaitGroup
	initial.Add(2)
	go func() {
		defer initial.Done()
		me, summonerErr = c.JSON(ctx, http.MethodGet, "/lol-summoner/v1/current-summoner", nil)
	}()
	go func() {
		defer initial.Done()
		loginQueue, queueErr = c.JSON(ctx, http.MethodGet, "/lol-login/v1/login-queue-state", nil)
	}()
	initial.Wait()
	if ctx.Err() != nil {
		c.disconnectForConnection(ctx, false)
		return ctx.Err()
	}
	if summonerErr != nil {
		var status *StatusError
		if !errors.As(summonerErr, &status) || status.Status == http.StatusUnauthorized || status.Status == http.StatusForbidden {
			c.disconnectForConnection(ctx, false)
			return summonerErr
		}
		// A summoner is unavailable during login/queueing. Confirm that the LCU
		// itself is responding before treating this as a connection failure.
		if queueErr != nil {
			if _, loginErr := c.JSON(ctx, http.MethodGet, "/lol-login/v1/session", nil); loginErr != nil {
				if _, phaseErr := c.JSON(ctx, http.MethodGet, "/lol-gameflow/v1/gameflow-phase", nil); phaseErr != nil {
					c.disconnectForConnection(ctx, false)
					return summonerErr
				}
			}
		}
		me = nil
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
			c.mu.Lock()
			if !c.connectionCurrentLocked(ctx) {
				c.mu.Unlock()
				return context.Canceled
			}
			c.auth = &copied
			c.mu.Unlock()
			auth = &copied
		}
	}
	c.setForConnection(ctx, "state", "connectionState", "connected")
	c.setForConnection(ctx, "state", "auth", auth.Public())
	c.setForConnection(ctx, "summoner", "me", me)
	if queueErr == nil {
		c.setForConnection(ctx, "login", "loginQueueState", loginQueue)
	} else {
		c.setForConnection(ctx, "login", "loginQueueState", nil)
	}
	type endpoint struct{ state, key, path string }
	endpoints := []endpoint{
		{"gameflow", "phase", "/lol-gameflow/v1/gameflow-phase"}, {"gameflow", "session", "/lol-gameflow/v1/session"},
		{"lobby", "lobby", "/lol-lobby/v2/lobby"}, {"matchmaking", "readyCheck", "/lol-matchmaking/v1/ready-check"},
		{"matchmaking", "search", "/lol-matchmaking/v1/search"}, {"champSelect", "session", "/lol-champ-select/v1/session"},
		{"chat", "me", "/lol-chat/v1/me"},
	}
	if me != nil {
		endpoints = append(endpoints, endpoint{"summoner", "profile", "/lol-summoner/v1/current-summoner/summoner-profile"})
	} else {
		c.setForConnection(ctx, "summoner", "profile", nil)
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
			if item.state == "champSelect" && item.key == "session" {
				c.setChampSelectSessionForConnection(ctx, value)
			} else {
				c.setForConnection(ctx, item.state, item.key, value)
			}
		}(item)
	}
	wg.Add(1)
	go func() {
		defer wg.Done()
		c.syncChat(ctx)
	}()
	wg.Wait()
	state := c.GameplayState()
	session := Map(Map(state["champSelect"])["session"])
	if len(session) > 0 {
		subset, err := c.JSON(ctx, http.MethodGet, subsetChampionListEndpoint, nil)
		if err != nil {
			subset = nil
		}
		c.setSubsetChampionListForConnection(ctx, subset)
		for _, item := range []endpoint{{"champSelect", "currentPickableChampionIds", "/lol-champ-select/v1/pickable-champion-ids"}, {"champSelect", "currentBannableChampionIds", "/lol-champ-select/v1/bannable-champion-ids"}, {"champSelect", "disabledChampionIds", "/lol-champ-select/v1/disabled-champion-ids"}, {"champSelect", "skinSelectorInfo", "/lol-champ-select/v1/skin-selector-info"}} {
			value, err := c.JSON(ctx, http.MethodGet, item.path, nil)
			if err != nil {
				value = nil
			}
			if item.key == "skinSelectorInfo" {
				c.setForConnection(ctx, item.state, item.key, value)
			} else {
				c.setForConnection(ctx, item.state, item.key, List(value))
			}
		}
	} else {
		c.clearChampSelectForConnection(ctx)
	}
	c.mu.RLock()
	loaded := c.assetsLoaded
	c.mu.RUnlock()
	if !loaded {
		c.loadAssets(ctx)
	}
	_ = c.RefreshTokens(ctx)
	return ctx.Err()
}

func (c *Client) disconnect() {
	c.disconnectForConnection(context.Background(), false)
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
			c.setForConnection(ctx, "gameData", key, entries)
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
		c.setForConnection(ctx, "gameData", "perkstyles", styles)
	}
	c.mu.Lock()
	if c.connectionCurrentLocked(ctx) {
		c.assetsLoaded = true
	}
	c.mu.Unlock()
}

// RegionConfig exposes endpoint configuration and readiness, never the bearer tokens.
func (c *Client) RegionConfig() map[string]any {
	serverID := c.CurrentServer()
	server, ok := c.options.Servers[serverID]
	c.mu.RLock()
	auth := map[string]any{}
	successes, failures := c.sgpConnectionSuccesses, c.sgpConnectionFailures
	if c.auth != nil {
		auth = c.auth.Public()
	}
	c.mu.RUnlock()
	return map[string]any{"leagueServers": map[string]any{"version": 2, "servers": c.Servers()}, "isTokenReady": c.TokenReady(), "isEntitlementsTokenSet": c.TokenReady(), "isLeagueSessionTokenSet": c.TokenReady(), "supportedQueues": []int{400, 420, 430, 440, 450, 700, 1700, 2400, 2401, 2403, 2405, 2410, 2450}, "availability": map[string]any{"sgpServerId": serverID, "region": auth["region"], "rsoPlatform": auth["rsoPlatformId"], "serversSupported": map[string]any{"matchHistory": ok && server.MatchHistory != "", "common": ok && server.Common != ""}}, "connectionSuccessesCounted": successes, "connectionFailuresCounted": failures}
}

func (c *Client) SGPState() map[string]any { return c.RegionConfig() }

func IsInGame(phase string) bool {
	return strings.Contains("|GameStart|InProgress|Reconnect|WaitingForStats|PreEndOfGame|EndOfGame|", "|"+phase+"|")
}
