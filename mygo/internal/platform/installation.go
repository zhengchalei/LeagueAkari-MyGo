package platform

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"slices"
	"strings"

	"github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"
)

func (s *Service) Refresh(ctx context.Context) error {
	processes, err := s.native.Processes(ctx)
	if err != nil {
		return err
	}
	auths := map[int]*client.Auth{}
	windowPIDs := map[int]int{}
	public := []any{}
	unreadable := false
	leaguePaths := []string{}
	streaming := []string{}
	for _, process := range processes {
		name := strings.ToLower(process.Name)
		if name == "leagueclientux.exe" || name == "leagueclient.exe" {
			if name == "leagueclient.exe" && process.Path != "" {
				leaguePaths = appendUnique(leaguePaths, process.Path)
			}
			auth := client.ParseCommandLine(process.CommandLine, process.PID)
			if auth == nil && process.Path != "" {
				if data, err := os.ReadFile(filepath.Join(filepath.Dir(process.Path), "lockfile")); err == nil {
					auth = client.ParseLockfile(string(data))
				}
			}
			if auth != nil {
				if auth.PID == 0 {
					auth.PID = process.PID
				}
				if _, exists := auths[auth.PID]; !exists || name == "leagueclientux.exe" {
					auths[auth.PID] = auth
					windowPIDs[auth.PID] = process.PID
				}
			} else if name == "leagueclientux.exe" {
				unreadable = true
			}
		}
		if name == "wegame.exe" || name == "tgp.exe" {
			if existingFile(process.Path) {
				s.set("client-installation-main", "weGameExecutablePath", process.Path)
			}
		}
		if name == "obs32.exe" || name == "obs64.exe" || name == "obs.exe" || name == "xsplit.core.exe" || name == "livehime.exe" || name == "yymixer.exe" || name == "douyutool.exe" || name == "huomaotool.exe" || name == "aliceincradle.exe" {
			streaming = appendUnique(streaming, process.Name)
		}
	}
	s.mu.Lock()
	s.auth = auths
	s.windowPIDs = windowPIDs
	s.processes = processes
	s.mu.Unlock()
	ids := []int{}
	for id := range auths {
		ids = append(ids, id)
	}
	slices.Sort(ids)
	for _, id := range ids {
		public = append(public, auths[id].Public())
	}
	s.set("league-client-ux-main", "launchedClients", public)
	s.set("league-client-ux-main", "hasClientButNoCommandLine", unreadable)
	s.set("client-installation-main", "detectedLiveStreamingClients", streaming)
	paths := []string{}
	if path, err := s.native.Registry(`Software\Tencent\LOL`, "InstallPath"); err == nil && path != "" {
		paths = append(paths, path)
	}
	for _, process := range processes {
		if strings.EqualFold(process.Name, "LeagueClient.exe") || strings.EqualFold(process.Name, "LeagueClientUx.exe") {
			for path := filepath.Dir(process.Path); path != "." && path != filepath.Dir(path); path = filepath.Dir(path) {
				if existingFile(filepath.Join(path, "LeagueClient", "LeagueClient.exe")) || existingFile(filepath.Join(path, "WeGameLauncher", "launcher.exe")) {
					paths = appendUnique(paths, path)
					break
				}
			}
		}
	}
	if s.State("client-installation-main")["tencentInstallationPath"] == nil {
		for _, drive := range s.native.Drives() {
			paths = appendUnique(paths, filepath.Join(drive, "WeGameApps", "英雄联盟"))
		}
	}
	for _, path := range paths {
		if info, err := os.Stat(path); err == nil && info.IsDir() {
			if existingFile(filepath.Join(path, "LeagueClient", "LeagueClient.exe")) {
				leaguePaths = appendUnique(leaguePaths, filepath.Join(path, "LeagueClient", "LeagueClient.exe"))
			}
			if existingFile(filepath.Join(path, "Launcher", "Client.exe")) || existingFile(filepath.Join(path, "WeGameLauncher", "launcher.exe")) {
				s.set("client-installation-main", "tencentInstallationPath", path)
				for key, suffix := range map[string]string{"tclsExecutablePath": filepath.Join("Launcher", "Client.exe"), "weGameLauncherExecutablePath": filepath.Join("WeGameLauncher", "launcher.exe")} {
					if exe := filepath.Join(path, suffix); existingFile(exe) {
						s.set("client-installation-main", key, exe)
					}
				}
			}
		}
	}
	if icon, err := s.native.Registry(`wegame\DefaultIcon`, ""); err == nil {
		path := strings.TrimSpace(icon)
		if strings.HasPrefix(path, `"`) {
			if end := strings.Index(path[1:], `"`); end >= 0 {
				path = path[1 : end+1]
			}
		} else if end := strings.LastIndex(path, ","); end >= 0 {
			path = path[:end]
		}
		if existingFile(path) {
			s.set("client-installation-main", "weGameExecutablePath", path)
		}
	}
	if programData := os.Getenv("ProgramData"); programData != "" {
		if data, err := os.ReadFile(filepath.Join(programData, "Riot Games", "RiotClientInstalls.json")); err == nil {
			var config struct {
				Associated map[string]string `json:"associated_client"`
			}
			if json.Unmarshal(data, &config) == nil {
				for path, riot := range config.Associated {
					if exe := filepath.Join(path, "LeagueClient.exe"); existingFile(exe) {
						leaguePaths = appendUnique(leaguePaths, exe)
					}
					if strings.Contains(riot, "Riot Games") && !strings.Contains(riot, "英雄联盟") && existingFile(riot) {
						s.set("client-installation-main", "officialRiotClientExecutablePath", riot)
					}
				}
			}
		}
	}
	s.set("client-installation-main", "leagueClientExecutablePaths", leaguePaths)
	s.set("app-common-main", "isElevated", s.IsElevated())
	s.set("app-common-main", "nativeSupport", s.NativeSupport())
	return nil
}

func existingFile(path string) bool {
	if path == "" {
		return false
	}
	info, err := os.Stat(path)
	return err == nil && info.Mode().IsRegular()
}
func appendUnique(values []string, value string) []string {
	for _, existing := range values {
		if strings.EqualFold(existing, value) {
			return values
		}
	}
	return append(values, value)
}
