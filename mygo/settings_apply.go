package main

import "github.com/zhengchalei/LeagueAkari-MyGo/mygo/internal/client"

// Imports notify a whole namespace. Apply its running configuration before
// publishing the new values so renderer switches match the host's behavior.
func (d *Desktop) settingChanged(namespace, key string) {
	changed := func(candidate string) bool { return key == "" || key == candidate }
	if namespace == "app-common-main" && changed("disableHardwareAcceleration") {
		config := object{"disableHardwareAcceleration": d.store.Get(namespace, "disableHardwareAcceleration") == true}
		d.setStatic(namespace+":state", "baseConfig", config)
		d.update(namespace, "state", "baseConfig", config)
	}
	if namespace == "in-game-send-main" {
		d.sendMu.Lock()
		d.syncSendShortcuts()
		d.sendMu.Unlock()
	}
	if namespace == "league-client-main" && changed("autoConnect") {
		d.client.SetAutoConnect(d.store.Get(namespace, "autoConnect") != false)
	}
	if namespace == "ongoing-game-main" && changed("matchHistoryLoadCount") {
		d.game.SetMatchHistoryLoadCount(int(client.Number(d.store.Get(namespace, "matchHistoryLoadCount"))))
	}
	if key == "" {
		for name, value := range d.store.Snapshot(namespace) {
			d.update(namespace, "settings", name, value)
		}
		return
	}
	d.update(namespace, "settings", key, d.store.Get(namespace, key))
}
