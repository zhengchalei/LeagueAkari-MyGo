package champion

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type JSONClient interface {
	JSON(context.Context, string, string, any) (any, error)
}

type gameplayClient interface {
	GameplayState() map[string]any
}

type selection struct {
	ChampionID int
	CellID     int
	GameID     int64
	Position   string
	Mode       string
	QueueType  string
}

type Runner struct {
	client          JSONClient
	settings        *settings.Store
	emit            bridge.Emitter
	tickMu          sync.Mutex
	updateMu        sync.Mutex
	mu              sync.Mutex
	activeCancel    context.CancelFunc
	reset           bool
	closed          bool
	unsubscribe     func()
	selectionKey    string
	conversationKey string
	connectionKey   string
	runesKey        string
	spellsKey       string
	state           map[string]any
}

// New performs no client writes; Run or Tick starts the optional automation.
func New(client JSONClient, store *settings.Store, emit bridge.Emitter) *Runner {
	r := &Runner{client: client, settings: store, emit: emit, state: map[string]any{
		"championId": 0, "runesStatus": "idle", "spellsStatus": "idle", "lastError": "",
	}}
	r.unsubscribe = store.OnChange(func(namespace, key string) {
		if namespace != Namespace {
			return
		}
		r.mu.Lock()
		if key == "enabled" || key == "" {
			r.reset = true
		}
		cancel := r.activeCancel
		r.mu.Unlock()
		if cancel != nil {
			cancel()
		}
	})
	return r
}

func (r *Runner) Close() {
	r.mu.Lock()
	r.closed = true
	cancel := r.activeCancel
	r.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if r.unsubscribe != nil {
		r.unsubscribe()
	}
}

func (r *Runner) Run(ctx context.Context) {
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for ctx.Err() == nil {
		_ = r.Tick(ctx)
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (r *Runner) State() map[string]any {
	r.mu.Lock()
	defer r.mu.Unlock()
	state := make(map[string]any, len(r.state))
	for key, value := range r.state {
		state[key] = value
	}
	return state
}

func (r *Runner) Tick(parent context.Context) error {
	r.tickMu.Lock()
	defer r.tickMu.Unlock()
	ctx, cancel := context.WithCancel(parent)
	if client, ok := r.client.(interface {
		RequestScope(context.Context) (context.Context, context.CancelFunc)
	}); ok {
		var release context.CancelFunc
		ctx, release = client.RequestScope(ctx)
		defer release()
	}
	r.mu.Lock()
	if r.closed {
		r.mu.Unlock()
		cancel()
		return context.Canceled
	}
	r.activeCancel = cancel
	if r.reset {
		r.clearSelection()
		r.reset = false
	}
	r.mu.Unlock()
	defer func() {
		cancel()
		r.mu.Lock()
		r.activeCancel = nil
		r.mu.Unlock()
	}()
	config, err := loadConfig(r.settings)
	if err != nil {
		return err
	}
	if client, ok := r.client.(gameplayClient); ok {
		state, _ := client.GameplayState()["state"].(map[string]any)
		auth, _ := json.Marshal(state["auth"])
		if r.connectionKey != string(auth) {
			r.mu.Lock()
			r.clearSelection()
			r.mu.Unlock()
			r.conversationKey = ""
			r.connectionKey = string(auth)
		}
	}
	if !config.Enabled {
		r.mu.Lock()
		r.clearSelection()
		r.mu.Unlock()
		return nil
	}
	self, err := r.currentSelection(ctx)
	if err != nil {
		r.mu.Lock()
		r.clearSelection()
		r.mu.Unlock()
		return err
	}
	if self == nil {
		r.conversationKey = ""
		r.mu.Lock()
		r.clearSelection()
		r.mu.Unlock()
		return nil
	}
	r.announce(ctx, *self, config)
	if self.ChampionID == 0 {
		r.mu.Lock()
		r.clearSelection()
		r.mu.Unlock()
		return nil
	}
	base := fmt.Sprintf("%d/%d/%d/%s/%s/%s", self.GameID, self.CellID, self.ChampionID, self.Position, self.Mode, self.QueueType)
	r.mu.Lock()
	if r.selectionKey != base {
		r.clearSelection()
		r.selectionKey = base
	}
	r.state["championId"] = self.ChampionID
	r.mu.Unlock()
	runes, spells := resolve(config, *self)
	r.mu.Lock()
	if runes == nil {
		r.runesKey = ""
		r.state["runesStatus"] = "idle"
	}
	if spells == nil {
		r.spellsKey = ""
		r.state["spellsStatus"] = "idle"
	}
	r.mu.Unlock()
	var pending sync.WaitGroup
	failures := make(chan error, 2)
	if runes != nil {
		fingerprint, _ := json.Marshal(runes)
		key := base + string(fingerprint)
		if r.claim("runes", key) {
			pending.Add(1)
			go func() {
				defer pending.Done()
				err := runes.validate()
				if err == nil {
					err = r.applyRunes(ctx, *self, runes)
				}
				if errors.Is(err, errNoRunePages) {
					r.mu.Lock()
					r.state["runesStatus"] = "idle"
					r.mu.Unlock()
					return
				}
				r.finish("runes", err)
				r.sendApplicationMessage(ctx, *self, "runes", runes, nil, err)
				if err != nil {
					failures <- err
				}
			}()
		}
	}
	if spells != nil && ctx.Err() == nil {
		fingerprint, _ := json.Marshal(spells)
		key := base + string(fingerprint)
		if r.claim("spells", key) {
			pending.Add(1)
			go func() {
				defer pending.Done()
				err := spells.validate()
				if err == nil {
					err = r.write(ctx, *self, http.MethodPatch, "/lol-champ-select/v1/session/my-selection", spells)
				}
				r.finish("spells", err)
				r.sendApplicationMessage(ctx, *self, "spells", nil, spells, err)
				if err != nil {
					failures <- err
				}
			}()
		}
	}
	pending.Wait()
	close(failures)
	var errorsReceived []error
	for err := range failures {
		errorsReceived = append(errorsReceived, err)
	}
	return errors.Join(errorsReceived...)
}

func (r *Runner) clearSelection() {
	r.selectionKey, r.runesKey, r.spellsKey = "", "", ""
	r.state["championId"] = 0
	r.state["runesStatus"], r.state["spellsStatus"], r.state["lastError"] = "idle", "idle", ""
}

func (r *Runner) claim(kind, key string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	seen := &r.runesKey
	if kind == "spells" {
		seen = &r.spellsKey
	}
	if *seen == key {
		return false
	}
	*seen = key
	r.state[kind+"Status"] = "applying"
	return true
}

func (r *Runner) finish(kind string, err error) {
	r.mu.Lock()
	if err == nil {
		r.state[kind+"Status"] = "applied"
	} else if errors.Is(err, context.Canceled) {
		r.state[kind+"Status"] = "idle"
		if kind == "runes" {
			r.runesKey = ""
		} else {
			r.spellsKey = ""
		}
	} else {
		r.state[kind+"Status"] = "error"
		r.state["lastError"] = err.Error()
	}
	r.mu.Unlock()
	if err != nil && !errors.Is(err, context.Canceled) && r.emit != nil {
		r.emit(Namespace, "error-"+kind+"-update", map[string]any{"message": err.Error()})
	}
}

func resolve(config Config, self selection) (*RunesConfig, *SpellsConfig) {
	id := strconv.Itoa(self.ChampionID)
	key := map[string]string{"CLASSIC": "normal", "ARAM": "aram", "KIWI": "aram", "URF": "urf", "NEXUSBLITZ": "nexusblitz", "ULTBOOK": "ultbook"}[self.Mode]
	if key == "" {
		return nil, nil
	}
	if self.Mode == "CLASSIC" && strings.HasPrefix(self.QueueType, "RANKED_") {
		key = "ranked-" + self.Position
		runes, spells := config.Runes[id][key], config.SummonerSpells[id][key]
		if runes == nil {
			runes = config.Runes[id]["ranked-default"]
		}
		if spells == nil {
			spells = config.SummonerSpells[id]["ranked-default"]
		}
		return runes, spells
	}
	return config.Runes[id][key], config.SummonerSpells[id][key]
}
