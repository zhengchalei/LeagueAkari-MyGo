package platform

import (
	"context"
	"net/http"
	"strings"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func (s *Service) followWindows(ctx context.Context) {
	s.followMu.Lock()
	defer s.followMu.Unlock()
	if s.options.Client == nil || s.options.Store == nil || s.options.WindowAction == nil {
		return
	}
	state := s.options.Client.GameplayState()
	phase := client.String(client.Map(state["gameflow"])["phase"])
	connected := client.Map(state["state"])["connectionState"] == "connected"
	s.mu.Lock()
	changed := s.lastPhase != phase
	s.lastPhase = phase
	s.mu.Unlock()
	for _, name := range []string{"aux-window", "opgg-window", "ongoing-game-window", "cd-timer-window"} {
		namespace := "window-manager-main/" + name
		enabled := s.Setting(namespace, "enabled") == true
		if !enabled {
			_, _ = s.options.WindowAction(name, "close", nil)
			continue
		}
		if name == "aux-window" {
			if !connected {
				_, _ = s.options.WindowAction(name, "hide", nil)
			} else if changed && s.options.Store.Get(namespace, "autoShow") == true {
				spectating := client.Map(client.Map(state["champSelect"])["session"])["isSpectating"] == true
				if phase == "ChampSelect" && spectating {
					continue
				}
				show := phase == "ChampSelect" || phase == "Lobby" || phase == "Matchmaking" || phase == "ReadyCheck"
				method := "hide"
				if show {
					method = "show"
				}
				_, _ = s.options.WindowAction(name, method, []any{true})
			}
		}
		if name == "opgg-window" && changed && phase == "ChampSelect" && s.options.Store.Get(namespace, "autoShow") == true {
			_, _ = s.options.WindowAction(name, "show", []any{true})
		}
		if name == "cd-timer-window" {
			session := client.Map(client.Map(state["gameflow"])["session"])
			mode := client.String(client.Map(client.Map(session["gameData"])["queue"])["gameMode"])
			supported := false
			for _, entry := range timerModes() {
				if client.Map(entry)["gameMode"] == mode {
					supported = true
					break
				}
			}
			use := phase == "InProgress" && supported
			if changed {
				method := "hide"
				if use {
					method = "show"
				}
				_, _ = s.options.WindowAction(name, method, []any{true})
			}
			if use && time.Since(s.lastTimerPoll) >= 4*time.Second {
				s.lastTimerPoll = time.Now()
				value, err := s.GameJSON(ctx, http.MethodGet, "/liveclientdata/gamestats", nil)
				var gameTime any
				if err == nil {
					gameTime = client.Map(value)["gameTime"]
				}
				s.set(namespace, "gameTime", gameTime)
			} else if !use {
				s.set(namespace, "gameTime", nil)
			}
		}
	}
}

func (s *Service) Setting(namespace, key string) any {
	if s.options.Store == nil {
		return nil
	}
	value := s.options.Store.Get(namespace, key)
	if value != nil {
		return value
	}
	if key == "enabled" && (namespace == "window-manager-main/aux-window" || namespace == "window-manager-main/opgg-window") {
		return true
	}
	if key == "opacity" {
		return 1.0
	}
	if key == "pinned" {
		return namespace != "window-manager-main/main-window"
	}
	return nil
}
func (s *Service) NotifyWindowState(namespace, key string, value any) {
	if !strings.HasPrefix(namespace, "window-manager-main/") {
		return
	}
	s.mu.Lock()
	if s.state[namespace] == nil {
		s.state[namespace] = map[string]any{}
	}
	s.state[namespace][key] = value
	s.mu.Unlock()
}
