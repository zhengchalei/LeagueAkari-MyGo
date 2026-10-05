package update

import (
	"path/filepath"
	"testing"
)

func TestUninstallOnlyAllowsPortableExecutablesAndExactUserData(t *testing.T) {
	configDirectory := t.TempDir()
	installationDirectory := t.TempDir()
	for _, name := range []string{executableName, legacyExecutableName} {
		for _, folder := range []string{"LeagueAkari-MyGo", "TimoMyGo"} {
			for _, removeData := range []bool{false, true} {
				if err := validateUninstallPaths(filepath.Join(installationDirectory, name), filepath.Join(configDirectory, folder), configDirectory, removeData); err != nil {
					t.Fatal(name, folder, removeData, err)
				}
			}
		}
	}
	for _, test := range []struct {
		target, data string
		removeData   bool
	}{
		{filepath.Join(installationDirectory, "League Akari.exe"), filepath.Join(configDirectory, "LeagueAkari-MyGo"), false},
		{filepath.Join(installationDirectory, executableName), configDirectory, true},
		{filepath.Join(installationDirectory, executableName), filepath.Join(configDirectory, "LeagueAkari-MyGo-backup"), true},
		{filepath.Join(installationDirectory, executableName), filepath.Join(configDirectory, "other", "LeagueAkari-MyGo"), true},
		{executableName, filepath.Join(configDirectory, "LeagueAkari-MyGo"), false},
		{filepath.Join(installationDirectory, executableName), "LeagueAkari-MyGo", false},
	} {
		if err := validateUninstallPaths(test.target, test.data, configDirectory, test.removeData); err == nil {
			t.Fatal("accepted invalid uninstall path", test)
		}
	}
}

func TestUpdateHelperRejectsElectronOrSourceOutsidePreparedDirectory(t *testing.T) {
	directory := t.TempDir()
	for _, test := range []struct{ prepared, target string }{
		{filepath.Join(directory, "new-updates", "prepared", executableName), filepath.Join(directory, "League Akari.exe")},
		{filepath.Join(directory, "outside", executableName), filepath.Join(directory, executableName)},
		{filepath.Join(directory, "new-updates", "prepared", "League Akari.exe"), filepath.Join(directory, executableName)},
	} {
		handled, err := RunHelper([]string{"--apply-update", "1", test.prepared, test.target, directory})
		if !handled || err == nil {
			t.Fatal("accepted invalid update source or target", test)
		}
	}
}
