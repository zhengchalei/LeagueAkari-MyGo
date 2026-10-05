package automation

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"reflect"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type JSONClient interface {
	JSON(context.Context, string, string, any) (any, error)
}

type GameflowSettings struct {
	AutoHonorEnabled                    bool              `json:"autoHonorEnabled"`
	AutoHonorStrategy                   string            `json:"autoHonorStrategy"`
	AutoAcceptEnabled                   bool              `json:"autoAcceptEnabled"`
	AutoAcceptDelaySeconds              float64           `json:"autoAcceptDelaySeconds"`
	PlayAgainEnabled                    bool              `json:"playAgainEnabled"`
	AutoReconnectEnabled                bool              `json:"autoReconnectEnabled"`
	AutoMatchmakingEnabled              bool              `json:"autoMatchmakingEnabled"`
	AutoMatchmakingDelaySeconds         float64           `json:"autoMatchmakingDelaySeconds"`
	AutoMatchmakingMinimumMembers       int               `json:"autoMatchmakingMinimumMembers"`
	AutoMatchmakingWaitForInvitees      bool              `json:"autoMatchmakingWaitForInvitees"`
	AutoMatchmakingRematchStrategy      string            `json:"autoMatchmakingRematchStrategy"`
	AutoMatchmakingRematchFixedDuration float64           `json:"autoMatchmakingRematchFixedDuration"`
	AutoSkipLeaderEnabled               bool              `json:"autoSkipLeaderEnabled"`
	AutoHandleInvitationsEnabled        bool              `json:"autoHandleInvitationsEnabled"`
	RejectInvitationWhenAway            bool              `json:"rejectInvitationWhenAway"`
	InvitationHandlingStrategies        map[string]string `json:"invitationHandlingStrategies"`
	AutoSendARAMTeamSideEnabled         bool              `json:"autoSendARAMTeamSideEnabled"`
	AutoSendARAMTeamSideVisibleToTeam   bool              `json:"autoSendARAMTeamSideVisibleToTeam"`
}

type scheduledAction struct {
	key       string
	due       time.Time
	attempted bool
}

// Runner starts no timers or LCU writes until its owner calls Run or Tick.
type Runner struct {
	client               JSONClient
	settings             *settings.Store
	emit                 bridge.Emitter
	now                  func() time.Time
	tickMu               sync.Mutex
	mu                   sync.Mutex
	emitMu               sync.Mutex
	publishedState       map[string]any
	activeCancel         context.CancelFunc
	revision             uint64
	seenRevision         uint64
	phase                string
	epoch                uint64
	scheduled            map[string]scheduledAction
	cancelled            map[string]bool
	state                map[string]map[string]any
	temporarilyDisabled  bool
	groups               []SelectGroup
	unsubscribe          func()
	honorGames           map[int64]*honorProgress
	queueProgress        queueProgress
	lastInvitationState  string
	lastLeaderState      string
	lastTeamSideState    string
	friendStates         map[string]string
	backloggedMessages   []string
	lastLocalMessage     string
	missionSkipAttempted bool
}

func New(client JSONClient, store *settings.Store, emit bridge.Emitter) *Runner {
	runner := &Runner{
		client: client, settings: store, emit: emit, now: time.Now,
		scheduled: map[string]scheduledAction{}, cancelled: map[string]bool{},
		honorGames:   map[int64]*honorProgress{},
		friendStates: map[string]string{},
		state: map[string]map[string]any{
			settings.GameflowNamespace: {
				"willAccept": false, "willAcceptAt": -1, "willSearchMatch": false,
				"willSearchMatchAt": -1, "willReconnectAt": -1, "activityStartStatus": "unavailable",
				"friendsToBeInvited": []string{},
			},
			settings.SelectNamespace: {
				"temporarilyDisabled": false, "delayedPick": nil, "delayedBan": nil,
				"delayedBenchSwap": nil, "delayedChampionSwap": nil,
				"groups": []SelectGroup{}, "activeGroupConfigId": nil,
				"expectedPicks": nil, "expectedBans": nil, "expectedSwaps": nil,
			},
		},
	}
	runner.unsubscribe = store.OnChange(func(namespace, key string) {
		if namespace == settings.GameflowNamespace || namespace == settings.SelectNamespace {
			runner.invalidate()
		}
	})
	return runner
}

func (runner *Runner) Close() {
	runner.invalidate()
	if runner.unsubscribe != nil {
		runner.unsubscribe()
	}
}

func (runner *Runner) Run(ctx context.Context) {
	ticker := time.NewTicker(750 * time.Millisecond)
	defer ticker.Stop()
	for {
		if ctx.Err() != nil {
			return
		}
		if err := runner.Tick(ctx); err != nil && !errors.Is(err, context.Canceled) {
			runner.notify(settings.GameflowNamespace, "error-automation", err.Error())
		}
		select {
		case <-ctx.Done():
			runner.invalidate()
			return
		case <-ticker.C:
		}
	}
}

func (runner *Runner) Tick(parent context.Context) error {
	runner.tickMu.Lock()
	defer runner.tickMu.Unlock()
	ctx, cancel := context.WithCancel(parent)
	runner.mu.Lock()
	runner.activeCancel = cancel
	if runner.seenRevision != runner.revision {
		runner.scheduled = map[string]scheduledAction{}
		runner.seenRevision = runner.revision
		runner.clearCountdownsLocked()
	}
	runner.mu.Unlock()
	defer func() {
		cancel()
		runner.mu.Lock()
		runner.activeCancel = nil
		runner.mu.Unlock()
		runner.publishState()
	}()
	var phase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &phase); err != nil {
		runner.mu.Lock()
		runner.phase = ""
		runner.scheduled = map[string]scheduledAction{}
		runner.clearCountdownsLocked()
		runner.mu.Unlock()
		return err
	}
	runner.mu.Lock()
	if phase != runner.phase {
		runner.phase = phase
		runner.epoch++
		runner.scheduled = map[string]scheduledAction{}
		runner.cancelled = map[string]bool{}
		runner.clearCountdownsLocked()
		if phase != "ChampSelect" {
			runner.state[settings.SelectNamespace]["activeGroupConfigId"] = nil
			runner.temporarilyDisabled = false
			runner.state[settings.SelectNamespace]["temporarilyDisabled"] = false
			runner.backloggedMessages = nil
			runner.lastLocalMessage = ""
			runner.lastTeamSideState = ""
		}
		if phase != "PreEndOfGame" {
			runner.missionSkipAttempted = false
		}
	}
	epoch := runner.epoch
	disabled := runner.temporarilyDisabled
	runner.mu.Unlock()
	var config GameflowSettings
	if err := runner.settings.Decode(settings.GameflowNamespace, &config); err != nil {
		return err
	}
	if err := runner.invitePendingFriends(ctx); err != nil {
		return err
	}
	if config.AutoHandleInvitationsEnabled {
		handled, err := runner.handleInvitation(ctx, config)
		if err != nil {
			return err
		}
		if handled {
			return nil
		}
	} else {
		runner.lastInvitationState = ""
	}
	if config.AutoSkipLeaderEnabled {
		handled, err := runner.transferLeader(ctx, config)
		if err != nil {
			return err
		}
		if handled {
			return nil
		}
	} else {
		runner.lastLeaderState = ""
	}
	if phase != "Matchmaking" {
		runner.queueProgress = queueProgress{}
	}
	switch phase {
	case "ReadyCheck":
		return runner.accept(ctx, phase, epoch, config)
	case "Reconnect":
		if config.AutoReconnectEnabled {
			return runner.phaseAction(ctx, "reconnect", phase, epoch, 10*time.Second, "/lol-gameflow/v1/reconnect", "autoReconnectEnabled")
		}
	case "WaitingForStats", "PreEndOfGame", "EndOfGame":
		return runner.endOfGame(ctx, phase, epoch, config)
	case "Lobby":
		return runner.matchmake(ctx, phase, epoch, config)
	case "Matchmaking":
		return runner.restartMatchmaking(ctx, config)
	case "ChampSelect":
		if config.AutoSendARAMTeamSideEnabled {
			if err := runner.sendTeamSide(ctx, config); err != nil {
				return err
			}
		}
		if !disabled {
			return runner.selectChampion(ctx, epoch)
		}
	}
	return nil
}

func (runner *Runner) State() map[string]any {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	result := map[string]any{}
	for namespace, state := range runner.state {
		data, _ := json.Marshal(state)
		var copied map[string]any
		_ = json.Unmarshal(data, &copied)
		result[namespace] = copied
	}
	return result
}

func (runner *Runner) publishState() {
	if runner.emit == nil {
		return
	}
	runner.emitMu.Lock()
	defer runner.emitMu.Unlock()
	current := runner.State()
	for namespace, snapshot := range current {
		previous, _ := runner.publishedState[namespace].(map[string]any)
		for key, value := range snapshot.(map[string]any) {
			old, exists := previous[key]
			if exists && reflect.DeepEqual(old, value) {
				continue
			}
			runner.emit("mobx-utils-main", "update-state-prop/"+namespace+":state", key, value, map[string]any{"action": "update", "raw": true})
		}
	}
	runner.publishedState = current
}

func (runner *Runner) SetTemporarilyDisabled(value bool) {
	runner.mu.Lock()
	runner.temporarilyDisabled = value
	runner.state[settings.SelectNamespace]["temporarilyDisabled"] = value
	if value {
		for _, operation := range []string{"pick", "vote", "ban", "bench", "trade"} {
			delete(runner.scheduled, operation)
			runner.clearOperationLocked(operation)
		}
	}
	runner.mu.Unlock()
	runner.invalidate()
	runner.publishState()
}

func (runner *Runner) SetSelectGroups(groups []SelectGroup) {
	data, _ := json.Marshal(groups)
	var copied []SelectGroup
	_ = json.Unmarshal(data, &copied)
	runner.mu.Lock()
	runner.groups = copied
	runner.state[settings.SelectNamespace]["groups"] = copied
	runner.mu.Unlock()
	runner.invalidate()
	runner.publishState()
}

func (runner *Runner) CancelAutoAccept()      { runner.cancel("accept") }
func (runner *Runner) CancelPlayAgain()       { runner.cancel("play-again") }
func (runner *Runner) CancelAutoMatchmaking() { runner.cancel("matchmake") }

func (runner *Runner) cancel(operation string) {
	runner.mu.Lock()
	runner.cancelled[operation] = true
	delete(runner.scheduled, operation)
	runner.clearOperationLocked(operation)
	if runner.activeCancel != nil {
		runner.activeCancel()
	}
	runner.mu.Unlock()
	runner.publishState()
}

func (runner *Runner) invalidate() {
	runner.mu.Lock()
	runner.revision++
	if runner.activeCancel != nil {
		runner.activeCancel()
	}
	runner.mu.Unlock()
}

func (runner *Runner) get(ctx context.Context, path string, target any) error {
	value, err := runner.client.JSON(ctx, "GET", path, nil)
	if err != nil {
		return err
	}
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}

func (runner *Runner) write(ctx context.Context, namespace, event, method, path string, body any) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	_, err := runner.client.JSON(ctx, method, path, body)
	if err != nil && !errors.Is(err, context.Canceled) {
		runner.notify(namespace, event, map[string]any{"message": err.Error()})
	}
	return err
}

func (runner *Runner) notify(namespace, event string, value any) {
	if runner.emit != nil {
		runner.emit(namespace, event, value)
	}
}

func (runner *Runner) ready(operation, key string, delay time.Duration) bool {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	if runner.cancelled[operation] {
		return false
	}
	task, exists := runner.scheduled[operation]
	if !exists || task.key != key {
		task = scheduledAction{key: key, due: runner.now().Add(max(delay, 0))}
		runner.scheduled[operation] = task
		runner.setCountdownLocked(operation, task.due.UnixMilli())
	}
	return !task.attempted && !runner.now().Before(task.due)
}

func (runner *Runner) markAttempted(operation string) {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	task := runner.scheduled[operation]
	task.attempted = true
	runner.scheduled[operation] = task
	runner.clearOperationLocked(operation)
}

func (runner *Runner) discardScheduled(operation string) {
	runner.mu.Lock()
	defer runner.mu.Unlock()
	delete(runner.scheduled, operation)
	runner.clearOperationLocked(operation)
}

func (runner *Runner) setCountdownLocked(operation string, due int64) {
	state := runner.state[settings.GameflowNamespace]
	switch operation {
	case "accept":
		state["willAccept"] = true
		state["willAcceptAt"] = due
	case "matchmake":
		state["willSearchMatch"] = true
		state["willSearchMatchAt"] = due
	case "reconnect":
		state["willReconnectAt"] = due
	}
}

func (runner *Runner) clearOperationLocked(operation string) {
	state := runner.state[settings.GameflowNamespace]
	switch operation {
	case "accept":
		state["willAccept"] = false
		state["willAcceptAt"] = -1
	case "matchmake":
		state["willSearchMatch"] = false
		state["willSearchMatchAt"] = -1
	case "reconnect":
		state["willReconnectAt"] = -1
	case "pick", "vote", "ban", "bench":
		field := map[string]string{"pick": "delayedPick", "vote": "delayedPick", "ban": "delayedBan", "bench": "delayedBenchSwap"}[operation]
		runner.state[settings.SelectNamespace][field] = nil
	case "trade":
		runner.state[settings.SelectNamespace]["delayedChampionSwap"] = nil
	}
}

func (runner *Runner) clearCountdownsLocked() {
	for _, operation := range []string{"accept", "matchmake", "reconnect", "pick", "vote", "ban", "bench", "trade"} {
		runner.clearOperationLocked(operation)
	}
}

func (runner *Runner) phaseAction(ctx context.Context, operation, phase string, epoch uint64, delay time.Duration, path, enabledKey string) error {
	if !runner.ready(operation, fmt.Sprint(epoch), delay) {
		return nil
	}
	if runner.settings.Get(settings.GameflowNamespace, enabledKey) != true {
		return nil
	}
	var currentPhase string
	if err := runner.get(ctx, "/lol-gameflow/v1/gameflow-phase", &currentPhase); err != nil {
		return err
	}
	if currentPhase != phase {
		return nil
	}
	runner.markAttempted(operation)
	event := map[string]string{"accept": "error-accept-match", "reconnect": "error-reconnect", "play-again": "error-play-again", "matchmake": "error-matchmaking"}[operation]
	return runner.write(ctx, settings.GameflowNamespace, event, "POST", path, nil)
}
