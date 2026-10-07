package client

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"reflect"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
)

// Auth stays in the host process; Public never returns either credential.
type Auth struct {
	PID        int
	Port       int
	Password   string
	Region     string
	PlatformID string
	BaseURL    string
}

func (a Auth) Public() map[string]any {
	return map[string]any{"pid": a.PID, "port": a.Port, "region": a.Region, "rsoPlatformId": a.PlatformID}
}

type Options struct {
	Discover      func(context.Context) (*Auth, error)
	HTTPClient    *http.Client
	SGPHTTPClient *http.Client
	Servers       map[string]Server
	PollInterval  time.Duration
}

type StatusError struct {
	Status   int
	Endpoint string
}

type HTTPError = StatusError

func (e *StatusError) StatusCode() int { return e.Status }

func (e *StatusError) Error() string {
	return fmt.Sprintf("客户端接口返回 %d (%s)", e.Status, e.Endpoint)
}

type Client struct {
	mu                                            sync.RWMutex
	stateUpdateMu                                 sync.Mutex
	tokensMu                                      sync.Mutex
	auth                                          *Auth
	entitlements                                  string
	leagueSession                                 string
	tokenTime                                     time.Time
	state                                         map[string]any
	emit                                          bridge.Emitter
	options                                       Options
	assetsLoaded                                  bool
	subscriptions                                 map[string]string
	subscriptionID                                uint64
	eventsConnected                               bool
	autoConnect                                   bool
	manualDisconnect                              bool
	connectionGeneration                          uint64
	connectionContext                             context.Context
	connectionCancel                              context.CancelFunc
	connectionChanged                             chan struct{}
	sgpConnectionSuccesses, sgpConnectionFailures int
	onEvent                                       func(uri, eventType string, data any)
}

func New(emit bridge.Emitter) *Client { return NewWithOptions(emit, Options{}) }

func NewWithOptions(emit bridge.Emitter, options Options) *Client {
	if options.Discover == nil {
		options.Discover = Discover
	}
	if options.HTTPClient == nil {
		options.HTTPClient = &http.Client{Timeout: 8 * time.Second, Transport: &http.Transport{
			TLSClientConfig:     &tls.Config{InsecureSkipVerify: true}, // LCU uses its local self-signed certificate.
			Proxy:               nil,
			MaxIdleConnsPerHost: 6,
		}}
	}
	if options.SGPHTTPClient == nil {
		options.SGPHTTPClient = &http.Client{Timeout: 15 * time.Second}
	}
	if options.Servers == nil {
		options.Servers = TencentServers()
	}
	if options.PollInterval <= 0 {
		options.PollInterval = 2 * time.Second
	}
	lifetime, cancel := context.WithCancel(context.Background())
	return &Client{options: options, state: initialState(), emit: emit, subscriptions: map[string]string{}, autoConnect: true, connectionContext: lifetime, connectionCancel: cancel, connectionChanged: make(chan struct{})}
}

func (c *Client) SetAutoConnect(enabled bool) {
	c.mu.Lock()
	if !c.autoConnect && enabled {
		c.manualDisconnect = false
	}
	if c.autoConnect != enabled && c.auth == nil {
		c.renewConnectionLocked()
	}
	c.autoConnect = enabled
	c.mu.Unlock()
	c.set("settings", "autoConnect", enabled)
}
func (c *Client) SetEventHandler(handler func(string, string, any)) {
	c.mu.Lock()
	c.onEvent = handler
	c.mu.Unlock()
}
func (c *Client) Connect(ctx context.Context, auth *Auth) error {
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if auth == nil {
		return errors.New("未发现可连接的客户端")
	}
	c.mu.Lock()
	c.manualDisconnect = false
	c.assignAuthLocked(auth, true)
	ctx, cancel := c.scopeConnectionLocked(ctx)
	c.mu.Unlock()
	defer cancel()
	defaults := initialState()
	for _, sub := range []string{"state", "gameflow", "champSelect", "lobbyTeamBuilder", "summoner", "login", "chat", "lobby", "matchmaking", "honor"} {
		for key, value := range Map(defaults[sub]) {
			c.setForConnection(ctx, sub, key, value)
		}
	}
	c.setForConnection(ctx, "state", "connectionState", "connecting")
	c.setForConnection(ctx, "state", "connectingClient", auth.Public())
	err := c.PollOnce(ctx)
	if err != nil {
		c.disconnectForConnection(ctx, false)
	}
	c.setForConnection(ctx, "state", "connectingClient", nil)
	return err
}
func (c *Client) Disconnect() { c.disconnectForConnection(context.Background(), true) }

func initialState() map[string]any {
	return map[string]any{
		"state":            map[string]any{"connectionState": "disconnected", "auth": nil, "connectingClient": nil},
		"settings":         map[string]any{"autoConnect": true},
		"gameflow":         map[string]any{"phase": nil, "session": nil},
		"champSelect":      map[string]any{"session": nil, "currentChampion": nil, "currentPickableChampionIds": []any{}, "currentBannableChampionIds": []any{}, "disabledChampionIds": []any{}, "ongoingChampionSwap": nil, "gridChampions": map[string]any{}, "skinSelectorInfo": nil},
		"summoner":         map[string]any{"me": nil, "profile": nil},
		"login":            map[string]any{"loginQueueState": nil},
		"lobby":            map[string]any{"lobby": nil, "receivedInvitations": []any{}},
		"matchmaking":      map[string]any{"readyCheck": nil, "search": nil},
		"honor":            map[string]any{"ballot": nil},
		"chat":             map[string]any{"me": nil, "conversations": map[string]any{"championSelect": nil, "postGame": nil, "customGame": nil}, "participants": map[string]any{"championSelect": nil, "postGame": nil, "customGame": nil}},
		"initialization":   map[string]any{"progress": nil},
		"lobbyTeamBuilder": map[string]any{"champSelect": map[string]any{"subsetChampionList": []any{}}},
		"gameData":         map[string]any{"champions": map[string]any{}, "queues": map[string]any{}, "items": map[string]any{}, "perks": map[string]any{}, "perkstyles": map[string]any{"schemaVersion": 0, "styles": map[string]any{}}, "augments": map[string]any{}, "summonerSpells": map[string]any{}, "gameModeMutators": map[string]any{}, "maps": map[string]any{}},
	}
}

func Clone(value any) any {
	switch value := value.(type) {
	case map[string]any:
		copy := make(map[string]any, len(value))
		for key, entry := range value {
			copy[key] = Clone(entry)
		}
		return copy
	case []any:
		copy := make([]any, len(value))
		for index, entry := range value {
			copy[index] = Clone(entry)
		}
		return copy
	default:
		return value
	}
}

func (c *Client) State() map[string]any {
	c.mu.RLock()
	defer c.mu.RUnlock()
	copy := make(map[string]any, len(c.state))
	for substate, value := range c.state {
		fields := Map(value)
		snapshot := make(map[string]any, len(fields))
		for key, entry := range fields {
			snapshot[key] = entry
		}
		copy[substate] = snapshot
	}
	return copy
}

// GameplayState excludes static asset catalogues when refreshing live roster data.
func (c *Client) GameplayState() map[string]any {
	c.mu.RLock()
	defer c.mu.RUnlock()
	view := map[string]any{}
	for _, key := range []string{"state", "gameflow", "champSelect", "summoner", "lobby"} {
		view[key] = Clone(c.state[key])
	}
	return view
}

func (c *Client) CurrentServer() string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	if c.auth == nil {
		return ""
	}
	if strings.EqualFold(c.auth.Region, "TENCENT") {
		return "TENCENT_" + strings.ToUpper(c.auth.PlatformID)
	}
	return strings.ToUpper(c.auth.Region)
}

func (c *Client) SetAuth(auth *Auth) {
	c.mu.Lock()
	c.assignAuthLocked(auth, false)
	c.mu.Unlock()
}
func (c *Client) assignAuthLocked(auth *Auth, force bool) {
	if !reflect.DeepEqual(c.auth, auth) {
		c.entitlements = ""
		c.leagueSession = ""
		c.tokenTime = time.Time{}
		c.assetsLoaded = false
	}
	if force || !reflect.DeepEqual(c.auth, auth) {
		c.renewConnectionLocked()
	}
	if auth == nil {
		c.auth = nil
	} else {
		copied := *auth
		c.auth = &copied
	}
}

func (c *Client) set(substate, key string, value any) {
	c.setForConnection(context.Background(), substate, key, value)
}
func (c *Client) setForConnection(ctx context.Context, substate, key string, value any) {
	c.stateUpdateMu.Lock()
	defer c.stateUpdateMu.Unlock()
	c.mu.Lock()
	if !c.connectionCurrentLocked(ctx) {
		c.mu.Unlock()
		return
	}
	fields := c.state[substate].(map[string]any)
	changed := !reflect.DeepEqual(fields[key], value)
	fields[key] = value
	c.mu.Unlock()
	if changed && c.emit != nil {
		c.emit("mobx-utils-main", "update-state-prop/league-client-main:"+substate, key, value, map[string]any{"action": "update", "raw": true})
	}
}

const subsetChampionListEndpoint = "/lol-lobby-team-builder/champ-select/v1/subset-champion-list"

func (c *Client) setSubsetChampionList(value any) {
	c.setSubsetChampionListForConnection(context.Background(), value)
}
func (c *Client) setSubsetChampionListForConnection(ctx context.Context, value any) {
	// Replace the nested object so both shallow-reactive renderer stores update.
	c.setForConnection(ctx, "lobbyTeamBuilder", "champSelect", map[string]any{"subsetChampionList": List(value)})
}

func (c *Client) setCurrentChampion(value any) {
	c.setCurrentChampionForConnection(context.Background(), value)
}
func (c *Client) setCurrentChampionForConnection(ctx context.Context, value any) {
	if champion := Number(value); champion > 0 {
		c.setForConnection(ctx, "champSelect", "currentChampion", champion)
	} else {
		c.setForConnection(ctx, "champSelect", "currentChampion", nil)
	}
}

func (c *Client) setChampSelectSession(value any) {
	c.setChampSelectSessionForConnection(context.Background(), value)
}
func (c *Client) setChampSelectSessionForConnection(ctx context.Context, value any) {
	if len(Map(value)) == 0 {
		c.clearChampSelectForConnection(ctx)
		return
	}
	c.setForConnection(ctx, "champSelect", "session", value)
	session := Map(value)
	var champion any
	for _, member := range List(session["myTeam"]) {
		row := Map(member)
		if Number(row["cellId"]) == Number(session["localPlayerCellId"]) {
			champion = row["championId"]
			break
		}
	}
	c.setCurrentChampionForConnection(ctx, champion)
}

func (c *Client) clearChampSelect() {
	c.clearChampSelectForConnection(context.Background())
}
func (c *Client) clearChampSelectForConnection(ctx context.Context) {
	for key, value := range Map(initialState()["champSelect"]) {
		c.setForConnection(ctx, "champSelect", key, value)
	}
	c.setSubsetChampionListForConnection(ctx, nil)
}

func encodeBody(body any) (io.Reader, error) {
	if body == nil {
		return nil, nil
	}
	switch value := body.(type) {
	case []byte:
		return bytes.NewReader(value), nil
	case json.RawMessage:
		return bytes.NewReader(value), nil
	case io.Reader:
		return value, nil
	default:
		data, err := json.Marshal(value)
		return bytes.NewReader(data), err
	}
}

func (c *Client) request(ctx context.Context, method, endpoint string, body any) (*http.Response, error) {
	c.mu.RLock()
	if !c.connectionCurrentLocked(ctx) {
		c.mu.RUnlock()
		return nil, context.Canceled
	}
	var auth *Auth
	if c.auth != nil {
		copied := *c.auth
		auth = &copied
	}
	if scope, ok := ctx.Value(connectionScopeKey{}).(connectionScope); ok && scope.auth != nil {
		copied := *scope.auth
		auth = &copied
	}
	c.mu.RUnlock()
	if auth == nil {
		return nil, errors.New("LOL 客户端尚未连接")
	}
	parsed, err := url.Parse(endpoint)
	if err != nil || parsed.IsAbs() || !strings.HasPrefix(endpoint, "/") || strings.HasPrefix(endpoint, "//") {
		return nil, errors.New("无效的客户端接口路径")
	}
	base := auth.BaseURL
	if base == "" {
		base = fmt.Sprintf("https://127.0.0.1:%d", auth.Port)
	}
	reader, err := encodeBody(body)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, method, strings.TrimRight(base, "/")+endpoint, reader)
	if err != nil {
		return nil, err
	}
	req.SetBasicAuth("riot", auth.Password)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	return c.options.HTTPClient.Do(req)
}

func (c *Client) JSON(ctx context.Context, method, endpoint string, body any) (any, error) {
	response, err := c.request(ctx, method, endpoint, body)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode >= 400 {
		return nil, &StatusError{response.StatusCode, strings.Split(endpoint, "?")[0]}
	}
	var value any
	err = json.NewDecoder(response.Body).Decode(&value)
	if errors.Is(err, io.EOF) {
		return nil, nil
	}
	return value, err
}

func (c *Client) JSONRaw(ctx context.Context, method, endpoint string, body any) (json.RawMessage, error) {
	response, err := c.request(ctx, method, endpoint, body)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode >= 400 {
		return nil, &StatusError{response.StatusCode, strings.Split(endpoint, "?")[0]}
	}
	data, err := io.ReadAll(response.Body)
	return json.RawMessage(data), err
}

// Proxy serves LCU image assets and API calls without passing credentials to the browser.
func (c *Client) Proxy(w http.ResponseWriter, r *http.Request) {
	endpoint := r.URL.Path
	if strings.Contains(endpoint, "/token") || endpoint == "/riotclient/auth-token" || strings.Contains(endpoint, "league-session-token") {
		http.Error(w, "登录凭据仅供原生后端使用", http.StatusForbidden)
		return
	}
	if r.URL.RawQuery != "" {
		endpoint += "?" + r.URL.RawQuery
	}
	var body any
	if r.Body != nil {
		data, err := io.ReadAll(io.LimitReader(r.Body, 8*1024*1024))
		if err != nil {
			http.Error(w, "无效请求", 400)
			return
		}
		if len(data) > 0 {
			body = data
		}
	}
	response, err := c.request(r.Context(), r.Method, endpoint, body)
	if err != nil {
		http.Error(w, "LOL 客户端尚未连接", http.StatusBadGateway)
		return
	}
	defer response.Body.Close()
	for _, key := range []string{"Content-Type", "Cache-Control", "ETag"} {
		if value := response.Header.Get(key); value != "" {
			w.Header().Set(key, value)
		}
	}
	w.WriteHeader(response.StatusCode)
	_, _ = io.Copy(w, response.Body)
}
