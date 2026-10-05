package platform

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type fakeNative struct {
	processes  []Process
	registry   map[string]string
	drives     []string
	foreground int
	elevated   map[int]bool
	launched   []string
	terminated []int
	input      []string
	keys       map[uint32]bool
	onInput    func(string)
	placement  *Placement
	repairs    int
}

func (n *fakeNative) Processes(context.Context) ([]Process, error) { return n.processes, nil }
func (n *fakeNative) Registry(path, name string) (string, error) {
	return n.registry[path+"/"+name], nil
}
func (n *fakeNative) Drives() []string { return n.drives }
func (n *fakeNative) Launch(path string, args []string) error {
	n.launched = append(n.launched, path)
	return nil
}
func (n *fakeNative) ForegroundPID() int                  { return n.foreground }
func (n *fakeNative) Terminate(pid int) error             { n.terminated = append(n.terminated, pid); return nil }
func (n *fakeNative) Elevated(pid int) bool               { return n.elevated[pid] }
func (n *fakeNative) Placement(int) (*Placement, error)   { return n.placement, nil }
func (n *fakeNative) Repair(int, float64, int, int) error { n.repairs++; return nil }
func (n *fakeNative) Key(code uint16, down bool) error {
	value := fmt.Sprintf("key:%d:%t", code, down)
	n.input = append(n.input, value)
	if n.onInput != nil {
		n.onInput(value)
	}
	return nil
}
func (n *fakeNative) Unicode(code uint16, down bool) error {
	value := fmt.Sprintf("unicode:%d:%t", code, down)
	n.input = append(n.input, value)
	if n.onInput != nil {
		n.onInput(value)
	}
	return nil
}
func (n *fakeNative) WatchKeys(ctx context.Context, callback func(uint32, bool)) error {
	<-ctx.Done()
	return ctx.Err()
}
func (n *fakeNative) KeyDown(code uint32) bool { return n.keys[code] }
func (n *fakeNative) SupportsMica() bool       { return true }
func (n *fakeNative) Supported() bool          { return true }

type fakeClient struct {
	state   map[string]any
	install string
}

func (c *fakeClient) GameplayState() map[string]any { return c.state }
func (c *fakeClient) JSON(ctx context.Context, method, path string, body any) (any, error) {
	switch path {
	case "/data-store/v1/install-dir":
		return c.install, nil
	case "/riotclient/zoom-scale":
		return 1.0, nil
	}
	return nil, fmt.Errorf("unexpected request %s", path)
}
func testStore(t *testing.T) *settings.Store {
	t.Helper()
	store, err := settings.New(filepath.Join(t.TempDir(), "settings.json"))
	if err != nil {
		t.Fatal(err)
	}
	return store
}
func storeSet(t *testing.T, store *settings.Store, ns, key string, value any) {
	t.Helper()
	if err := store.Set(ns, key, value); err != nil {
		t.Fatal(err)
	}
}
func gameNative() *fakeNative {
	return &fakeNative{processes: []Process{{PID: 100, Name: "League of Legends.exe"}, {PID: 200, Name: "editor.exe"}}, foreground: 100, elevated: map[int]bool{}, keys: map[uint32]bool{}}
}

func TestClientDiscoveryKeepsCredentialsPrivateAndSelectsByPID(t *testing.T) {
	root := t.TempDir()
	launcher := filepath.Join(root, "WeGameLauncher", "launcher.exe")
	if err := os.MkdirAll(filepath.Dir(launcher), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(launcher, []byte("fake"), 0600); err != nil {
		t.Fatal(err)
	}
	native := gameNative()
	native.registry = map[string]string{`Software\Tencent\LOL/InstallPath`: root}
	native.processes = append(native.processes, Process{PID: 11, Name: "LeagueClientUx.exe", CommandLine: `--app-port=1234 --app-pid=11 --remoting-auth-token=private-one --region=TENCENT --rso_platform_id=NJ100`}, Process{PID: 22, Name: "LeagueClientUx.exe", CommandLine: `--app-port=1235 --app-pid=22 --remoting-auth-token=private-two --region=TENCENT --rso_platform_id=HN1`})
	service := New(Options{Native: native})
	defer service.Close()
	if err := service.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	wire, _ := json.Marshal(service.State("league-client-ux-main"))
	if strings.Contains(string(wire), "private-") || strings.Contains(string(wire), "authToken") {
		t.Fatal("client credentials leaked into renderer state")
	}
	if len(client.List(service.State("league-client-ux-main")["launchedClients"])) != 2 {
		t.Fatal("multiple launched clients not exposed")
	}
	auth, err := service.ResolveAuth(context.Background(), map[string]any{"pid": 22})
	if err != nil || auth.Password != "private-two" || auth.Port != 1235 {
		t.Fatal("selected client's private auth was not resolved")
	}
	if _, err := service.Call(context.Background(), "client-installation-main", "launchWeGameLeagueOfLegends", nil); err != nil {
		t.Fatal(err)
	}
	if len(native.launched) != 1 || native.launched[0] != launcher {
		t.Fatal("installation launcher used the wrong file")
	}
	native.elevated[100] = true
	if client.Map(service.NativeSupport()["nativeInput"])["available"] != false {
		t.Fatal("higher-permission game incorrectly claims native input")
	}
}

func TestKeyboardShortcutPressReleaseAndOriginalRegistrationContract(t *testing.T) {
	native := gameNative()
	events := []string{}
	service := New(Options{Native: native, Emit: func(ns, name string, args ...any) {
		if ns == "keyboard-shortcuts-main" {
			events = append(events, name)
		}
	}})
	defer service.Close()
	pressed := []bool{}
	if err := service.RegisterTarget("overlay", "Control+A", "stateful", func(details ShortcutDetails) { pressed = append(pressed, details.Pressed) }); err != nil {
		t.Fatal(err)
	}
	if err := service.RegisterTarget("other", "Control+A", "normal", nil); err == nil {
		t.Fatal("shortcut target conflict not reported")
	}
	service.handleKey(162, true)
	service.handleKey(65, true)
	service.handleKey(65, true)
	service.handleKey(162, false)
	service.handleKey(65, false)
	if !slices.Equal(pressed, []bool{true, false}) {
		t.Fatalf("stateful shortcut lost release or duplicated autorepeat: %v", pressed)
	}
	if !slices.Contains(events, "shortcut") || !slices.Contains(events, "last-active-shortcut") {
		t.Fatal("renderer shortcut recording events were lost")
	}
	registration, err := service.Call(context.Background(), "keyboard-shortcuts-main", "getRegistrationByTargetId", []any{"overlay"})
	if err != nil || client.Map(registration)["shortcutId"] != "Control+A" {
		t.Fatal("registration IPC shape changed")
	}
	released := 0
	if err := service.RegisterTarget("preset", "LeftControl+B", "last-active", func(details ShortcutDetails) {
		if details.Pressed {
			t.Fatal("last-active fired on press")
		}
		released++
	}); err != nil {
		t.Fatal(err)
	}
	service.handleKey(162, true)
	service.handleKey(66, true)
	if released != 0 {
		t.Fatal("last-active fired before release")
	}
	service.handleKey(66, false)
	service.handleKey(162, false)
	if released != 1 {
		t.Fatal("last-active callback missing")
	}
	if err := service.RegisterTarget("enter", "Enter", "normal", nil); err == nil {
		t.Fatal("reserved Enter was registered")
	}
	if !service.UnregisterTarget("overlay") {
		t.Fatal("unregister failed")
	}
}

func TestInputSendsUnicodeLinesOnlyWhileLOLIsForegroundAndCancels(t *testing.T) {
	for _, scenario := range []string{"success", "not-foreground", "lost-foreground", "cancel"} {
		t.Run(scenario, func(t *testing.T) {
			native := gameNative()
			service := New(Options{Native: native})
			defer service.Close()
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			if scenario == "not-foreground" {
				native.foreground = 200
			}
			if scenario == "lost-foreground" || scenario == "cancel" {
				native.onInput = func(value string) {
					if strings.HasPrefix(value, "unicode:") && strings.HasSuffix(value, "true") {
						if scenario == "cancel" {
							cancel()
						} else {
							native.foreground = 200
						}
					}
				}
			}
			err := service.SendLines(ctx, []string{"阿狸", "second"})
			if scenario == "success" {
				if err != nil {
					t.Fatal(err)
				}
				if !slices.Contains(native.input, "unicode:38463:true") || len(native.input) != 24 {
					t.Fatalf("Unicode line sequence incorrect: %v", native.input)
				}
			} else {
				if err == nil {
					t.Fatal("unsafe/cancelled input returned success")
				}
				if scenario == "not-foreground" && len(native.input) != 0 {
					t.Fatal("input was sent into another app")
				}
				if slices.Contains(native.input, "unicode:115:true") {
					t.Fatal("continued to second line after cancellation/foreground change")
				}
			}
			if len(native.input) > 0 && native.input[len(native.input)-1] == "key:13:true" {
				t.Fatal("Enter left pressed after cancellation")
			}
		})
	}
}

func TestTerminateAndConfigWriteAreScopedToSelectedGameAndInstall(t *testing.T) {
	root := t.TempDir()
	leagueDir := filepath.Join(root, "LeagueClient")
	config := filepath.Join(root, "Game", "Config", "PersistedSettings.json")
	if err := os.MkdirAll(filepath.Dir(config), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(config, []byte("{}"), 0600); err != nil {
		t.Fatal(err)
	}
	backend := &fakeClient{install: leagueDir, state: map[string]any{"state": map[string]any{"auth": map[string]any{"region": "TENCENT"}}}}
	native := gameNative()
	service := New(Options{Native: native, Client: backend})
	defer service.Close()
	if err := service.TerminateGame(context.Background()); err != nil || !slices.Equal(native.terminated, []int{100}) {
		t.Fatal("did not terminate only foreground game")
	}
	native.foreground = 200
	if err := service.TerminateGame(context.Background()); err == nil || len(native.terminated) != 1 {
		t.Fatal("terminated a background/non-game process")
	}
	if mode, err := service.configMode(context.Background()); err != nil || mode != "writable" {
		t.Fatal("config readonly query failed")
	}
	if err := service.setConfigMode(context.Background(), "readonly"); err != nil {
		t.Fatal(err)
	}
	if mode, err := service.configMode(context.Background()); err != nil || mode != "readonly" {
		t.Fatal("readonly mode did not apply to mock file")
	}
	if err := service.setConfigMode(context.Background(), "writable"); err != nil {
		t.Fatal(err)
	}
	directory := filepath.Join(filepath.Dir(config), "Global", "Recommended")
	if err := os.MkdirAll(directory, 0700); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"akari1-old.json", "timo1-old.json", "user-set.json"} {
		if err := os.WriteFile(filepath.Join(directory, name), []byte("{}"), 0600); err != nil {
			t.Fatal(err)
		}
	}
	items := []any{map[string]any{"uid": "akari1-new", "title": "test", "blocks": []any{}}}
	if err := service.writeItemSets(context.Background(), items, true); err != nil {
		t.Fatal(err)
	}
	if !existingFile(filepath.Join(directory, "user-set.json")) || !existingFile(filepath.Join(directory, "akari1-new.json")) || existingFile(filepath.Join(directory, "akari1-old.json")) {
		t.Fatal("item-set cleanup scope incorrect")
	}
	if err := service.writeItemSets(context.Background(), []any{map[string]any{"uid": "../escape"}}, true); err == nil {
		t.Fatal("unsafe item-set path accepted")
	}
	if !existingFile(filepath.Join(directory, "akari1-new.json")) {
		t.Fatal("validation happened after clearing existing configs")
	}
}

func TestGameProxyMaintainsBodyAndStatusContract(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/liveclientdata/gamestats" {
			w.Header().Set("Content-Type", "application/json")
			fmt.Fprint(w, `{"gameTime":123.5}`)
			return
		}
		w.WriteHeader(404)
	}))
	defer server.Close()
	service := New(Options{Native: gameNative(), GameHTTPClient: server.Client(), GameBaseURL: server.URL})
	defer service.Close()
	value, err := service.GameJSON(context.Background(), http.MethodGet, "/liveclientdata/gamestats", nil)
	if err != nil || client.Map(value)["gameTime"] != 123.5 {
		t.Fatal("game stats JSON failed")
	}
	request := httptest.NewRequest(http.MethodGet, "/missing", nil)
	response := httptest.NewRecorder()
	service.GameProxy(response, request)
	if response.Code != 404 {
		t.Fatal("proxy changed game API status")
	}
	if _, err := service.GameJSON(context.Background(), http.MethodGet, "https://example.com/", nil); err == nil {
		t.Fatal("game proxy allowed an external endpoint")
	}
}

func TestPeekClientReadsSelectedSummonerAndImageWithoutSwitchingCurrentClient(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		username, password, ok := r.BasicAuth()
		if !ok || username != "riot" || password != "private-test" {
			t.Error("private LCU auth missing")
		}
		if r.URL.Path == "/lol-summoner/v1/current-summoner" {
			fmt.Fprint(w, `{"puuid":"selected","profileIconId":5}`)
		} else {
			w.Header().Set("Content-Type", "image/jpeg")
			_, _ = w.Write([]byte{1, 2, 3})
		}
	}))
	defer server.Close()
	parsed, _ := url.Parse(server.URL)
	port, _ := strconv.Atoi(parsed.Port())
	native := gameNative()
	native.processes = append(native.processes, Process{PID: 11, Name: "LeagueClientUx.exe", CommandLine: fmt.Sprintf("--app-port=%d --app-pid=11 --remoting-auth-token=private-test", port)})
	backend := &fakeClient{state: map[string]any{"state": map[string]any{"auth": map[string]any{"pid": 22}}}}
	service := New(Options{Native: native, Client: backend, GameHTTPClient: server.Client()})
	defer service.Close()
	peek, err := service.peekClient(context.Background(), map[string]any{"pid": 11})
	if err != nil || client.Map(client.Map(peek)["summoner"])["puuid"] != "selected" || client.Map(peek)["profileIcon"] != "data:image/jpeg;base64,AQID" {
		t.Fatalf("peek did not return summoner and image: %v", err)
	}
	if client.Number(client.Map(client.Map(backend.state["state"])["auth"])["pid"]) != 22 {
		t.Fatal("peek changed selected connection")
	}
}

func TestWindowAutomationShowsOnPhaseChangeAndDisabledTimersStayHidden(t *testing.T) {
	store := testStore(t)
	for _, name := range []string{"aux-window", "opgg-window", "cd-timer-window"} {
		storeSet(t, store, "window-manager-main/"+name, "enabled", true)
		storeSet(t, store, "window-manager-main/"+name, "autoShow", true)
	}
	backend := &fakeClient{state: map[string]any{"state": map[string]any{"connectionState": "connected"}, "gameflow": map[string]any{"phase": "ChampSelect"}, "champSelect": map[string]any{"session": map[string]any{}}}}
	actions := []string{}
	service := New(Options{Native: gameNative(), Store: store, Client: backend, WindowAction: func(name, method string, args []any) (any, error) {
		actions = append(actions, name+"/"+method)
		return nil, nil
	}})
	defer service.Close()
	service.followWindows(context.Background())
	service.followWindows(context.Background())
	if count(actions, "aux-window/show") != 1 || count(actions, "opgg-window/show") != 1 || count(actions, "cd-timer-window/show") != 0 {
		t.Fatalf("phase repeated auto-show or CD timer visible outside game: %v", actions)
	}
	for _, action := range actions {
		if strings.HasSuffix(action, "/ensure") {
			t.Fatal("automation pre-created a window instead of showing it on demand")
		}
	}
	client.Map(backend.state["gameflow"])["phase"] = "InProgress"
	service.followWindows(context.Background())
	if count(actions, "aux-window/hide") != 1 {
		t.Fatal("aux window did not hide entering game")
	}
	storeSet(t, store, "window-manager-main/aux-window", "enabled", false)
	if count(actions, "aux-window/close") == 0 {
		t.Fatal("disabled window not closed")
	}
}

func TestEnabledWindowsDoNotCreateOrShowWithoutAutoShow(t *testing.T) {
	store := testStore(t)
	for _, name := range []string{"aux-window", "opgg-window", "ongoing-game-window"} {
		storeSet(t, store, "window-manager-main/"+name, "enabled", true)
		storeSet(t, store, "window-manager-main/"+name, "autoShow", false)
	}
	backend := &fakeClient{state: map[string]any{"state": map[string]any{"connectionState": "connected"}, "gameflow": map[string]any{"phase": "ChampSelect"}}}
	actions := []string{}
	service := New(Options{Native: gameNative(), Store: store, Client: backend, WindowAction: func(name, method string, args []any) (any, error) {
		actions = append(actions, name+"/"+method)
		return nil, nil
	}})
	defer service.Close()
	for _, phase := range []string{"ChampSelect", "InProgress", "InProgress", "EndOfGame", "Lobby"} {
		client.Map(backend.state["gameflow"])["phase"] = phase
		service.followWindows(context.Background())
	}
	storeSet(t, store, "window-manager-main/aux-window", "enabled", true)
	for _, action := range actions {
		if strings.HasSuffix(action, "/ensure") || strings.HasSuffix(action, "/show") {
			t.Fatalf("enabled without autoShow exposed a window: %v", actions)
		}
	}
}
func count(values []string, value string) int {
	result := 0
	for _, entry := range values {
		if entry == value {
			result++
		}
	}
	return result
}

func TestCancelSendReleasesPendingEnter(t *testing.T) {
	native := gameNative()
	service := New(Options{Native: native})
	defer service.Close()
	started := make(chan struct{})
	var once sync.Once
	native.onInput = func(value string) {
		if value == "key:13:true" {
			once.Do(func() { close(started) })
		}
	}
	result := make(chan error, 1)
	go func() { result <- service.SendText(context.Background(), "must-cancel") }()
	<-started
	service.CancelSend()
	select {
	case err := <-result:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected cancel result: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("native send did not cancel")
	}
	if !slices.Equal(native.input, []string{"key:13:true", "key:13:false"}) {
		t.Fatalf("cancellation continued typing: %v", native.input)
	}
}

func TestDisableHardwareAccelerationPersistsAndReportsRestart(t *testing.T) {
	store := testStore(t)
	service := New(Options{Native: gameNative(), Store: store})
	defer service.Close()
	result, err := service.Call(context.Background(), "app-common-main", "setDisableHardwareAcceleration", []any{true})
	if err != nil || store.Get("app-common-main", "disableHardwareAcceleration") != true || client.Map(result)["restartRequired"] != true {
		t.Fatal("GPU setting did not persist/restart contract missing")
	}
}

func TestNativeReadOnly(t *testing.T) {
	if os.Getenv("TIMO_NATIVE_READONLY") != "1" {
		t.Skip("explicit native read-only verification")
	}
	service := New(Options{})
	defer service.Close()
	if err := service.Refresh(context.Background()); err != nil {
		t.Fatal(err)
	}
	clients := client.List(service.State("league-client-ux-main")["launchedClients"])
	t.Logf("native discovery returned %d clients; elevated=%t; mica=%t", len(clients), service.IsElevated(), service.native.SupportsMica())
	if len(clients) > 0 {
		auth, err := service.ResolveAuth(context.Background(), clients[0])
		if err != nil || auth.Password == "" {
			t.Fatal("discovered client auth unavailable")
		}
		placement, err := service.ClientPlacement(context.Background())
		if err != nil {
			t.Fatal(err)
		}
		if placement != nil {
			t.Logf("client placement: width=%d height=%d minimized=%t", placement.Width, placement.Height, placement.IsMinimized)
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Millisecond)
	defer cancel()
	if err := service.native.WatchKeys(ctx, func(uint32, bool) {}); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("read-only keyboard listener setup/teardown failed: %v", err)
	}
	t.Log("native keyboard listener installed and removed; no input injected")
}
