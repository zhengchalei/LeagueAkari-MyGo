package platform

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"sync"
	"time"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/bridge"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/settings"
)

type JSONClient interface {
	JSON(context.Context, string, string, any) (any, error)
	GameplayState() map[string]any
}

type Process struct {
	PID                     int
	Name, Path, CommandLine string
}

type Placement struct {
	Left        int  `json:"left"`
	Top         int  `json:"top"`
	Right       int  `json:"right"`
	Bottom      int  `json:"bottom"`
	Width       int  `json:"width"`
	Height      int  `json:"height"`
	ShownState  int  `json:"shownState"`
	IsMinimized bool `json:"isMinimized"`
	IsMaximized bool `json:"isMaximized"`
	IsNormal    bool `json:"isNormal"`
}

type Native interface {
	Processes(context.Context) ([]Process, error)
	Registry(string, string) (string, error)
	Drives() []string
	Launch(string, []string) error
	ForegroundPID() int
	Terminate(int) error
	Elevated(int) bool
	Placement(int) (*Placement, error)
	Repair(int, float64, int, int) error
	Key(uint16, bool) error
	Unicode(uint16, bool) error
	WatchKeys(context.Context, func(uint32, bool)) error
	KeyDown(uint32) bool
	SupportsMica() bool
	Supported() bool
}

type Options struct {
	Client         JSONClient
	Store          *settings.Store
	Emit           bridge.Emitter
	Native         Native
	WindowAction   func(namespace, method string, args []any) (any, error)
	GameHTTPClient *http.Client
	GameBaseURL    string
}

type Service struct {
	options                  Options
	native                   Native
	mu                       sync.RWMutex
	state                    map[string]map[string]any
	auth                     map[int]*client.Auth
	windowPIDs               map[int]int
	processes                []Process
	shortcuts                map[string]*registration
	targets                  map[string]string
	pressed                  map[uint32]bool
	lastCodes, statefulCodes []uint32
	lastPhase                string
	lastTimerPoll            time.Time
	hookReady                bool
	hookError                string
	unsubscribe              func()
	inputMu                  sync.Mutex
	followMu                 sync.Mutex
	inputCancel              context.CancelFunc
	hookCancel               context.CancelFunc
	closed                   bool
}

func New(options Options) *Service {
	if options.Native == nil {
		options.Native = newNative()
	}
	if options.GameHTTPClient == nil {
		options.GameHTTPClient = gameHTTPClient()
	}
	if options.GameBaseURL == "" {
		options.GameBaseURL = "https://127.0.0.1:2999"
	}
	s := &Service{options: options, native: options.Native, auth: map[int]*client.Auth{}, windowPIDs: map[int]int{}, shortcuts: map[string]*registration{}, targets: map[string]string{}, pressed: map[uint32]bool{}, state: map[string]map[string]any{
		"client-installation-main":            {"leagueClientExecutablePaths": []string{}, "tencentInstallationPath": nil, "tclsExecutablePath": nil, "weGameLauncherExecutablePath": nil, "weGameExecutablePath": nil, "officialRiotClientExecutablePath": nil, "detectedLiveStreamingClients": []string{}},
		"league-client-ux-main":               {"launchedClients": []any{}, "hasClientButNoCommandLine": false},
		"window-manager-main":                 {"supportsMica": options.Native.SupportsMica(), "isManagerFinishedInit": true},
		"window-manager-main/cd-timer-window": {"supportedGameModes": timerModes(), "gameTime": nil},
	}}
	if options.Store != nil {
		s.unsubscribe = options.Store.OnChange(func(namespace, key string) { s.settingChanged(namespace, key) })
	}
	return s
}

func (s *Service) Run(ctx context.Context) {
	hookContext, stop := context.WithCancel(ctx)
	s.mu.Lock()
	s.hookCancel = stop
	s.mu.Unlock()
	go func() {
		s.mu.Lock()
		s.hookReady = s.native.Supported()
		s.mu.Unlock()
		if err := s.native.WatchKeys(hookContext, s.handleKey); err != nil && !errors.Is(err, context.Canceled) {
			s.mu.Lock()
			s.hookReady = false
			s.hookError = err.Error()
			s.mu.Unlock()
			if s.options.Emit != nil {
				s.options.Emit("keyboard-shortcuts-main", "error-native-keyboard", err.Error())
			}
		}
	}()
	_ = s.Refresh(ctx)
	s.applyShortcutSettings()
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			s.Close()
			return
		case <-ticker.C:
			_ = s.Refresh(ctx)
			s.followWindows(ctx)
		}
	}
}

func (s *Service) Close() {
	s.mu.Lock()
	s.closed = true
	stop := s.hookCancel
	cancel := s.inputCancel
	s.shortcutClearLocked()
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if stop != nil {
		stop()
	}
	if s.unsubscribe != nil {
		s.unsubscribe()
	}
}

func (s *Service) State(namespace string) map[string]any {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := map[string]any{}
	for key, value := range s.state[namespace] {
		out[key] = value
	}
	return out
}

func (s *Service) set(namespace, key string, value any) {
	s.mu.Lock()
	if s.state[namespace] == nil {
		s.state[namespace] = map[string]any{}
	}
	changed := !reflect.DeepEqual(s.state[namespace][key], value)
	s.state[namespace][key] = value
	s.mu.Unlock()
	if changed && s.options.Emit != nil {
		s.options.Emit("mobx-utils-main", "update-state-prop/"+namespace+":state", key, value, map[string]any{"action": "update", "raw": true})
	}
}

func (s *Service) IsElevated() bool { return s.native.Elevated(os.Getpid()) }

func (s *Service) NativeSupport() map[string]any {
	requires := false
	s.mu.RLock()
	processes := append([]Process(nil), s.processes...)
	hookError := s.hookError
	s.mu.RUnlock()
	for _, process := range processes {
		if strings.EqualFold(process.Name, "League of Legends.exe") && s.native.Elevated(process.PID) && !s.IsElevated() {
			requires = true
		}
	}
	platform := s.native.Supported()
	out := map[string]any{}
	for _, key := range []string{"nativeInput", "getLeagueClientWindowPlacement", "adjustLeagueClientWindowSize", "isProcessForeground"} {
		available := platform
		needs := false
		if key == "nativeInput" {
			available = platform && !requires && hookError == ""
			needs = requires
		}
		if key == "adjustLeagueClientWindowSize" {
			s.mu.RLock()
			for pid := range s.auth {
				if s.native.Elevated(pid) && !s.IsElevated() {
					available = false
					needs = true
				}
			}
			s.mu.RUnlock()
		}
		out[key] = map[string]any{"available": available, "availableOnCurrentPlatform": platform, "requiresElevation": needs}
	}
	return out
}

func (s *Service) Call(ctx context.Context, namespace, method string, args []any) (any, error) {
	switch namespace {
	case "client-installation-main":
		if method == "update" {
			return nil, s.Refresh(ctx)
		}
		key := ""
		launchArgs := []string{}
		switch method {
		case "launchTencentTcls":
			key = "tclsExecutablePath"
		case "launchWeGame":
			key = "weGameExecutablePath"
		case "launchWeGameLeagueOfLegends":
			key = "weGameLauncherExecutablePath"
		case "launchDefaultRiotClient":
			key = "officialRiotClientExecutablePath"
			launchArgs = []string{"--launch-product=league_of_legends", "--launch-patchline=live"}
		}
		if key != "" {
			if err := s.Refresh(ctx); err != nil {
				return nil, err
			}
			path, _ := s.State(namespace)[key].(string)
			if path == "" {
				return nil, errors.New("未检测到对应客户端安装路径")
			}
			return nil, s.native.Launch(path, launchArgs)
		}
	case "league-client-ux-main":
		if method == "update" {
			return nil, s.Refresh(ctx)
		}
		if method == "rebuildWmi" {
			return nil, s.rebuildWMI()
		}
	case "app-common-main":
		if method == "setDisableHardwareAcceleration" {
			if s.options.Store == nil {
				return nil, errors.New("设置服务未初始化")
			}
			disabled := false
			if len(args) > 0 {
				disabled, _ = args[0].(bool)
			}
			if err := s.options.Store.Set(namespace, "disableHardwareAcceleration", disabled); err != nil {
				return nil, err
			}
			s.set(namespace, "disableHardwareAcceleration", disabled)
			return map[string]any{"restartRequired": true, "message": "重启 LeagueAkari-MyGo 后生效"}, nil
		}
		if method == "relaunchAsAdministrator" {
			if s.IsElevated() {
				return nil, errors.New("LeagueAkari-MyGo 已以管理员身份运行")
			}
			if err := s.relaunchElevated(); err != nil {
				return nil, err
			}
			if s.options.WindowAction != nil {
				_, err := s.options.WindowAction("main-window", "quit", nil)
				return nil, err
			}
			return nil, errors.New("管理员实例已启动，请关闭当前实例")
		}
	case "league-client-main":
		if method == "peekClient" {
			var value any
			if len(args) > 0 {
				value = args[0]
			}
			return s.peekClient(ctx, value)
		}
		if method == "writeItemSetsToDisk" {
			var items any
			if len(args) > 0 {
				items = args[0]
			}
			clearPrevious := true
			if len(args) > 1 {
				if value, ok := args[1].(bool); ok {
					clearPrevious = value
				}
			}
			return nil, s.writeItemSets(ctx, items, clearPrevious)
		}
		if method == "fixWindowMethodA" {
			auth, err := s.ResolveAuth(ctx, s.currentAuth())
			if err != nil {
				return nil, err
			}
			zoom, err := s.options.Client.JSON(ctx, http.MethodGet, "/riotclient/zoom-scale", nil)
			if err != nil {
				return nil, err
			}
			width, height := 1280, 720
			if len(args) > 0 {
				config := client.Map(args[0])
				if v := client.Number(config["baseWidth"]); v > 0 {
					width = int(v)
				}
				if v := client.Number(config["baseHeight"]); v > 0 {
					height = int(v)
				}
			}
			value, ok := zoom.(float64)
			if !ok {
				return nil, errors.New("客户端缩放比例无效")
			}
			return nil, s.native.Repair(s.windowPID(auth.PID), value, width, height)
		}
		if method == "getWindowPlacement" {
			return s.ClientPlacement(ctx)
		}
	case "game-client-main":
		switch method {
		case "terminateGameClient":
			return nil, s.TerminateGame(ctx)
		case "setSettingsFileReadonlyOrWritable":
			return nil, s.setConfigMode(ctx, textArg(args, 0))
		case "getSettingsFileReadonlyOrWritable":
			return s.configMode(ctx)
		case "isGameClientForeground":
			return s.IsGameForeground(ctx)
		}
	case "keyboard-shortcuts-main":
		return s.shortcutCall(method, args)
	case "window-manager-main/cd-timer-window":
		if method == "sendInGame" {
			return nil, s.SendText(ctx, textArg(args, 0))
		}
	}
	return nil, fmt.Errorf("未知原生操作: %s.%s", namespace, method)
}

func (s *Service) currentAuth() any {
	if s.options.Client == nil {
		return nil
	}
	return client.Map(s.options.Client.GameplayState()["state"])["auth"]
}

func (s *Service) ResolveAuth(ctx context.Context, value any) (*client.Auth, error) {
	fields := client.Map(value)
	pid := int(client.Number(fields["pid"]))
	port := int(client.Number(fields["port"]))
	if err := s.Refresh(ctx); err != nil {
		return nil, err
	}
	s.mu.RLock()
	for id, auth := range s.auth {
		if (pid != 0 && id == pid) || (pid == 0 && port != 0 && auth.Port == port) {
			copy := *auth
			s.mu.RUnlock()
			return &copy, nil
		}
	}
	s.mu.RUnlock()
	password := client.String(fields["authToken"])
	if password != "" && port > 0 && port <= 65535 {
		return &client.Auth{PID: pid, Port: port, Password: password, Region: client.String(fields["region"]), PlatformID: client.String(fields["rsoPlatformId"])}, nil
	}
	return nil, errors.New("所选客户端已退出或无法读取连接信息")
}

func (s *Service) ClientPlacement(ctx context.Context) (*Placement, error) {
	if err := s.Refresh(ctx); err != nil {
		return nil, err
	}
	pid := int(client.Number(client.Map(s.currentAuth())["pid"]))
	if pid == 0 {
		s.mu.RLock()
		for id := range s.auth {
			pid = id
			break
		}
		s.mu.RUnlock()
	}
	return s.native.Placement(s.windowPID(pid))
}

func (s *Service) windowPID(pid int) int {
	s.mu.RLock()
	defer s.mu.RUnlock()
	if owner := s.windowPIDs[pid]; owner != 0 {
		return owner
	}
	return pid
}

func (s *Service) IsGameForeground(ctx context.Context) (bool, error) {
	processes, err := s.native.Processes(ctx)
	if err != nil {
		return false, err
	}
	foreground := s.native.ForegroundPID()
	for _, process := range processes {
		if process.PID == foreground && strings.EqualFold(process.Name, "League of Legends.exe") {
			return true, nil
		}
	}
	return false, nil
}

func (s *Service) TerminateGame(ctx context.Context) error {
	processes, err := s.native.Processes(ctx)
	if err != nil {
		return err
	}
	foreground := s.native.ForegroundPID()
	for _, process := range processes {
		if process.PID == foreground && strings.EqualFold(process.Name, "League of Legends.exe") {
			return s.native.Terminate(process.PID)
		}
	}
	return errors.New("LOL 游戏未处于前台")
}

func (s *Service) configPath(ctx context.Context) (string, error) {
	if s.options.Client == nil {
		return "", errors.New("客户端未连接")
	}
	root, err := s.options.Client.JSON(ctx, http.MethodGet, "/data-store/v1/install-dir", nil)
	if err != nil {
		return "", err
	}
	path, ok := root.(string)
	if !ok || !filepath.IsAbs(path) {
		return "", errors.New("客户端安装路径无效")
	}
	region := client.String(client.Map(s.currentAuth())["region"])
	if strings.EqualFold(region, "TENCENT") {
		return filepath.Join(path, "..", "Game", "Config", "PersistedSettings.json"), nil
	}
	return filepath.Join(path, "Config", "PersistedSettings.json"), nil
}

func (s *Service) setConfigMode(ctx context.Context, mode string) error {
	path, err := s.configPath(ctx)
	if err != nil {
		return err
	}
	switch mode {
	case "", "readonly":
		return os.Chmod(path, 0444)
	case "writable":
		return os.Chmod(path, 0644)
	}
	return errors.New("配置文件模式必须为 readonly 或 writable")
}
func (s *Service) configMode(ctx context.Context) (string, error) {
	path, err := s.configPath(ctx)
	if err != nil {
		return "", err
	}
	info, err := os.Stat(path)
	if err != nil {
		return "", err
	}
	if info.Mode()&0222 != 0 {
		return "writable", nil
	}
	return "readonly", nil
}

func textArg(args []any, index int) string {
	if index < len(args) {
		return client.String(args[index])
	}
	return ""
}
func timerModes() []any {
	out := []any{}
	for _, mode := range []string{"CLASSIC", "PRACTICETOOL", "ARAM", "URF", "ONEFORALL", "NEXUSBLITZ", "ULTBOOK", "KIWI"} {
		haste := 0
		if mode == "ARAM" || mode == "KIWI" {
			haste = 70
		} else if mode == "URF" {
			haste = 300
		}
		out = append(out, map[string]any{"gameMode": mode, "abilityHaste": haste})
	}
	return out
}
func decode(value, target any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	return json.Unmarshal(data, target)
}
