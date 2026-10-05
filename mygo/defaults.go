package main

import (
	_ "embed"
	"encoding/json"
)

//go:embed internal/bridge/assets/static-data.json
var staticData []byte

//go:embed internal/bridge/assets/select-groups.json
var selectGroups []byte

func staticStates() map[string]object {
	states := map[string]object{}
	_ = json.Unmarshal(staticData, &states)
	nativeSupport := object{}
	for _, key := range []string{"nativeInput", "getLeagueClientWindowPlacement", "adjustLeagueClientWindowSize", "isProcessForeground"} {
		nativeSupport[key] = object{"available": false, "availableOnCurrentPlatform": false, "requiresElevation": false}
	}
	states["app-common-main:state"] = object{"platform": "win32", "isElevated": false, "shouldUseDarkColors": false, "isRunInTempDir": false, "startupDeepLink": nil, "baseConfig": nil, "nativeSupport": nativeSupport, "disableHardwareAcceleration": false}
	states["logger-factory-main:state"] = object{"logLevel": "info"}
	states["storage-main:state"] = object{"usingHigherVersionDb": false}
	states["window-manager-main:state"] = object{"supportsMica": false, "downloadTasks": object{}}
	for _, name := range []string{"main-window", "aux-window", "opgg-window", "ongoing-game-window", "cd-timer-window"} {
		states["window-manager-main/"+name+":state"] = object{"status": "normal", "focus": "focused", "bounds": object{"x": 0, "y": 0, "width": 1200, "height": 820}, "show": name == "main-window", "ready": name == "main-window", "fakeShow": false, "trackedBounds": object{}, "supportedGameModes": []any{}, "gameTime": 0}
	}
	states["extra-assets-main:gtimg"] = object{"heroList": nil, "kiwiAugments": nil}
	states["extra-assets-main:fandom"] = object{"balance": nil}
	states["client-installation-main:state"] = object{"leagueClientExecutablePaths": []any{}, "tencentInstallationPath": "", "weGameExecutablePath": "", "officialRiotClientExecutablePath": "", "tclsExecutablePath": "", "weGameLauncherExecutablePath": "", "detectedLiveStreamingClients": []any{}}
	states["league-client-ux-main:state"] = object{"launchedClients": []any{}, "hasClientButNoCommandLine": false}
	states["renderer-debug-main:state"] = object{"sendAllNativeLcuEvents": false, "rules": []any{}, "logAllLcuEvents": false}
	states["respawn-timer-main:state"] = object{"info": object{"timeLeft": 0, "totalTime": 0, "isDead": false}}
	states["self-update-main:state"] = object{"lastCheckAt": 0, "updateProgressInfo": nil, "lastUpdateResult": nil}
	states["in-game-send-main:state"] = object{"ratingPuuids": []any{}, "junglePuuids": []any{}, "premadeIndices": []any{}}
	return states
}
