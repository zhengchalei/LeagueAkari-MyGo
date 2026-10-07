package main

import (
	"path/filepath"
	"testing"
)

func TestTempDirectoryNotificationUsesExecutableBoundary(t *testing.T) {
	base := t.TempDir()
	for _, tt := range []struct {
		path     string
		expected bool
	}{
		{filepath.Join(base, "LeagueAkari.WinUI.exe"), true},
		{filepath.Join(base, "unpacked", "LeagueAkari.WinUI.exe"), true},
		{filepath.Join(base+"-application", "LeagueAkari.WinUI.exe"), false},
		{"", false},
	} {
		if got := isExecutableInTemporaryDirectory(tt.path, base); got != tt.expected {
			t.Fatalf("path %q got %v expected %v", tt.path, got, tt.expected)
		}
	}
}
func TestWinUITempNotificationChecksHostRatherThanBackendExecutable(t *testing.T) {
	temp := t.TempDir()
	t.Setenv("TEMP", temp)
	t.Setenv("TMP", temp)
	d := &Desktop{winUIHostExecutable: filepath.Join(temp, "unzipped", "LeagueAkari.WinUI.exe"), static: staticStates()}
	d.initializeStartupDiagnostics()
	if d.static["app-common-main:state"]["isRunInTempDir"] != true {
		t.Fatal("WinUI host temporary location did not trigger warning state")
	}
}
