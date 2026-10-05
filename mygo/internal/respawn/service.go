package respawn

import (
	"context"
	"encoding/json"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type JSONClient interface {
	JSON(context.Context, string, string, any) (any, error)
}
type GameJSONClient interface {
	GameJSON(context.Context, string, string, any) (any, error)
}
type Info struct {
	TimeLeft  float64 `json:"timeLeft"`
	TotalTime float64 `json:"totalTime"`
	IsDead    bool    `json:"isDead"`
}
type Service struct {
	lcu    JSONClient
	game   GameJSONClient
	store  *settings.Store
	emit   bridge.Emitter
	mu     sync.RWMutex
	tickMu sync.Mutex
	info   Info
}

func New(lcu JSONClient, game GameJSONClient, store *settings.Store, emit bridge.Emitter) *Service {
	return &Service{lcu: lcu, game: game, store: store, emit: emit}
}
func (service *Service) Run(ctx context.Context) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		if ctx.Err() != nil {
			return
		}
		_ = service.Tick(ctx)
		select {
		case <-ctx.Done():
			service.set(Info{})
			return
		case <-ticker.C:
		}
	}
}
func (service *Service) State() map[string]any {
	service.mu.RLock()
	defer service.mu.RUnlock()
	return map[string]any{settings.RespawnNamespace: map[string]any{"info": service.info}}
}
func (service *Service) Close() { service.set(Info{}) }

func (service *Service) Tick(ctx context.Context) error {
	service.tickMu.Lock()
	defer service.tickMu.Unlock()
	if service.store.Get(settings.RespawnNamespace, "enabled") != true {
		service.set(Info{})
		return nil
	}
	value, err := service.lcu.JSON(ctx, "GET", "/lol-gameflow/v1/gameflow-phase", nil)
	if err != nil {
		service.set(Info{})
		return err
	}
	if value != "InProgress" {
		service.set(Info{})
		return nil
	}
	var summoner struct {
		GameName     string `json:"gameName"`
		TagLine      string `json:"tagLine"`
		DisplayName  string `json:"displayName"`
		InternalName string `json:"internalName"`
	}
	value, err = service.lcu.JSON(ctx, "GET", "/lol-summoner/v1/current-summoner", nil)
	if err != nil {
		return err
	}
	if err := decode(value, &summoner); err != nil {
		return err
	}
	name := summoner.GameName
	if name != "" && summoner.TagLine != "" {
		name += "#" + summoner.TagLine
	} else if summoner.DisplayName != "" {
		name = summoner.DisplayName
	} else {
		name = summoner.InternalName
	}
	var players []struct {
		RiotID       string  `json:"riotId"`
		SummonerName string  `json:"summonerName"`
		IsDead       bool    `json:"isDead"`
		RespawnTimer float64 `json:"respawnTimer"`
	}
	value, err = service.game.GameJSON(ctx, "GET", "/liveclientdata/playerlist", nil)
	if err != nil {
		return err
	}
	if err := decode(value, &players); err != nil {
		return err
	}
	for _, player := range players {
		matches := player.RiotID != "" && strings.EqualFold(player.RiotID, name)
		if player.RiotID == "" {
			matches = strings.EqualFold(player.SummonerName, name) || strings.EqualFold(player.SummonerName, summoner.InternalName)
		}
		if !matches {
			continue
		}
		service.mu.RLock()
		previous := service.info
		service.mu.RUnlock()
		total := previous.TotalTime
		if !previous.IsDead && player.IsDead {
			total = player.RespawnTimer
		}
		if service.store.Get(settings.RespawnNamespace, "enabled") != true {
			service.set(Info{})
			return nil
		}
		service.set(Info{TimeLeft: max(player.RespawnTimer, 0), TotalTime: total, IsDead: player.IsDead})
		return nil
	}
	return nil
}

func (service *Service) set(info Info) {
	service.mu.Lock()
	changed := service.info != info
	service.info = info
	service.mu.Unlock()
	if changed && service.emit != nil {
		service.emit("mobx-utils-main", "update-state-prop/respawn-timer-main:state", "info", info, map[string]any{"action": "update", "raw": true})
	}
}
func decode(value, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}
