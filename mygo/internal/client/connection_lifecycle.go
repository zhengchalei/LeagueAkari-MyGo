package client

import (
	"context"
	"reflect"
)

type connectionScopeKey struct{}
type connectionScope struct {
	generation uint64
	auth       *Auth
}

// RequestScope keeps a multi-request read on one client generation. Switching
// clients or disconnecting cancels both its in-flight and subsequent requests.
func (c *Client) RequestScope(ctx context.Context) (context.Context, context.CancelFunc) {
	return c.scopeConnection(ctx)
}

func (c *Client) renewConnectionLocked() {
	if c.connectionCancel != nil {
		c.connectionCancel()
	}
	c.connectionGeneration++
	if c.connectionChanged != nil {
		close(c.connectionChanged)
	}
	c.connectionChanged = make(chan struct{})
	c.eventsConnected = false
	c.connectionContext, c.connectionCancel = context.WithCancel(context.Background())
}
func (c *Client) scopeConnection(ctx context.Context) (context.Context, context.CancelFunc) {
	if _, ok := ctx.Value(connectionScopeKey{}).(connectionScope); ok {
		return ctx, func() {}
	}
	c.mu.RLock()
	defer c.mu.RUnlock()
	return c.scopeConnectionLocked(ctx)
}
func (c *Client) scopeConnectionLocked(ctx context.Context) (context.Context, context.CancelFunc) {
	scope := connectionScope{generation: c.connectionGeneration}
	if c.auth != nil {
		copied := *c.auth
		scope.auth = &copied
	}
	scoped, cancel := context.WithCancel(context.WithValue(ctx, connectionScopeKey{}, scope))
	stop := context.AfterFunc(c.connectionContext, cancel)
	return scoped, func() { stop(); cancel() }
}
func (c *Client) connectionCurrentLocked(ctx context.Context) bool {
	if ctx.Err() != nil {
		return false
	}
	return c.connectionGenerationMatchesLocked(ctx)
}
func (c *Client) connectionGenerationMatchesLocked(ctx context.Context) bool {
	scope, scoped := ctx.Value(connectionScopeKey{}).(connectionScope)
	return !scoped || scope.generation == c.connectionGeneration
}

// Cancellation and state reset share the same lock, so an old failed poll can
// neither reconnect a disconnected client nor disconnect a newly selected PID.
func (c *Client) disconnectForConnection(ctx context.Context, manual bool) {
	c.stateUpdateMu.Lock()
	defer c.stateUpdateMu.Unlock()
	c.mu.Lock()
	if !c.connectionGenerationMatchesLocked(ctx) {
		c.mu.Unlock()
		return
	}
	if manual {
		c.manualDisconnect = true
	}
	hadSGPCounts := c.sgpConnectionSuccesses != 0 || c.sgpConnectionFailures != 0
	c.sgpConnectionSuccesses, c.sgpConnectionFailures = 0, 0
	c.assignAuthLocked(nil, true)
	defaults := initialState()
	type change struct {
		sub, key string
		value    any
	}
	changes := []change{}
	for _, sub := range []string{"state", "gameflow", "champSelect", "lobbyTeamBuilder", "summoner", "login", "chat", "lobby", "matchmaking", "honor"} {
		fields := Map(c.state[sub])
		for key, value := range Map(defaults[sub]) {
			if !reflect.DeepEqual(fields[key], value) {
				changes = append(changes, change{sub, key, value})
			}
			fields[key] = value
		}
	}
	c.mu.Unlock()
	if c.emit != nil {
		if hadSGPCounts {
			for _, key := range []string{"connectionSuccessesCounted", "connectionFailuresCounted"} {
				c.emit("mobx-utils-main", "update-state-prop/sgp-main:state", key, 0, map[string]any{"action": "update", "raw": true})
			}
		}
		for _, item := range changes {
			c.emit("mobx-utils-main", "update-state-prop/league-client-main:"+item.sub, item.key, item.value, map[string]any{"action": "update", "raw": true})
		}
	}
}

func (c *Client) discoveredConnection(ctx, parent context.Context, auth *Auth) (context.Context, context.CancelFunc, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.connectionCurrentLocked(ctx) || c.manualDisconnect || !c.autoConnect || c.auth != nil {
		return ctx, func() {}, context.Canceled
	}
	c.assignAuthLocked(auth, false)
	// Bind to the discovered generation without retaining its cancelled prior
	// discovery context; its caller's cancellation remains in force.
	bound, cancel := c.scopeConnectionLocked(parent)
	return bound, cancel, nil
}
