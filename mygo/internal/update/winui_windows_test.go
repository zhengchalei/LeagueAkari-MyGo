//go:build windows

package update

import (
	"os"
	"path/filepath"
	"testing"
)

func TestWinUIApplyValidatesFullScopedInstallation(t *testing.T) {
	base := t.TempDir()
	installation := filepath.Join(base, "app")
	data := filepath.Join(base, "profile")
	stage := filepath.Join(data, "new-updates", "prepared")
	for _, directory := range []string{installation, stage} {
		os.MkdirAll(directory, 0700)
		for _, name := range winUIRequiredFiles {
			os.WriteFile(filepath.Join(directory, name), []byte("nonempty"), 0600)
		}
	}
	if err := validateWinUIApplyPaths(stage, filepath.Join(installation, WinUIExecutable), data); err != nil {
		t.Fatal(err)
	}
	if err := validateWinUIApplyPaths(installation, filepath.Join(installation, WinUIExecutable), data); err == nil {
		t.Fatal("download source outside scoped staging accepted")
	}
	if err := validateWinUIApplyPaths(stage, filepath.Join(base, WinUIExecutable), data); err == nil {
		t.Fatal("non WinUI installation accepted")
	}
	copy := filepath.Join(base, "installed")
	if err := copyWinUIDirectory(stage, copy); err != nil {
		t.Fatal(err)
	}
	if err := validateWinUILayout(copy); err != nil {
		t.Fatal(err)
	}
}
