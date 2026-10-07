//go:build windows

package update

import (
	"os"
	"path/filepath"
	"testing"
)

func uninstallFixture(t *testing.T) (string, string) {
	t.Helper()
	root := t.TempDir()
	installation, data := filepath.Join(root, "LeagueAkari-WinUI-0.5.6-win-x64"), filepath.Join(root, "profile")
	for _, path := range []string{installation, data} {
		if err := os.MkdirAll(path, 0700); err != nil {
			t.Fatal(err)
		}
	}
	for _, name := range winUIRequiredFiles {
		os.WriteFile(filepath.Join(installation, name), []byte("fixture"), 0600)
	}
	os.WriteFile(filepath.Join(installation, WinUIInstallMarker), []byte(winUIInstallIdentity), 0600)
	return filepath.Join(installation, WinUIExecutable), data
}

func TestWinUIUninstallRemovesOnlyDedicatedInstallationAndOwnedData(t *testing.T) {
	for _, removeData := range []bool{false, true} {
		host, data := uninstallFixture(t)
		os.WriteFile(filepath.Join(data, "settings.json"), []byte("config"), 0600)
		os.WriteFile(filepath.Join(data, "unrelated.txt"), []byte("keep"), 0600)
		if err := removeWinUIInstallation(host, data, removeData); err != nil {
			t.Fatal(err)
		}
		if _, err := os.Stat(filepath.Dir(host)); !os.IsNotExist(err) {
			t.Fatal("installation retained")
		}
		if _, err := os.Stat(filepath.Join(data, "unrelated.txt")); err != nil {
			t.Fatal("unrelated profile file deleted")
		}
		_, err := os.Stat(filepath.Join(data, "settings.json"))
		if removeData != os.IsNotExist(err) {
			t.Fatal("removeData intent not respected")
		}
	}
}

func TestWinUIUninstallRejectsSourceIncompleteOverlappingAndUnmarkedPaths(t *testing.T) {
	for _, kind := range []string{"source", "missing-pri", "unmarked", "overlap", "root-data"} {
		t.Run(kind, func(t *testing.T) {
			host, data := uninstallFixture(t)
			switch kind {
			case "source":
				os.WriteFile(filepath.Join(filepath.Dir(host), "package.json"), []byte("{}"), 0600)
			case "missing-pri":
				os.Remove(filepath.Join(filepath.Dir(host), "LeagueAkari.WinUI.pri"))
			case "unmarked":
				os.Remove(filepath.Join(filepath.Dir(host), WinUIInstallMarker))
			case "overlap":
				data = filepath.Dir(host)
			case "root-data":
				data = filepath.VolumeName(host) + string(filepath.Separator)
			}
			if err := removeWinUIInstallation(host, data, true); err == nil {
				t.Fatal("unsafe uninstall accepted")
			}
			if _, err := os.Stat(host); err != nil {
				t.Fatal("rejected uninstall changed installation")
			}
		})
	}
}
