package update

import (
	"archive/zip"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

const WinUIExecutable = "LeagueAkari.WinUI.exe"
const WinUIBackendExecutable = "LeagueAkari.Backend.exe"

var winUIRequiredFiles = []string{WinUIExecutable, WinUIBackendExecutable, "LeagueAkari.WinUI.dll", "LeagueAkari.WinUI.deps.json", "LeagueAkari.WinUI.runtimeconfig.json", "LeagueAkari.WinUI.pri", "App.xbf", "coreclr.dll", "hostfxr.dll", "Microsoft.UI.Xaml.dll", "Microsoft.WindowsAppRuntime.dll", "league-akari-winui.portable"}

func winUIArchivePriority(name, version string) int {
	name = strings.ToLower(name)
	version = strings.ToLower(strings.TrimPrefix(version, "v"))
	if name == "leagueakari-winui-"+version+"-win-x64.zip" {
		return 3
	}
	return 0
}

func prepareWinUIDirectory(archive, destination string) (string, error) {
	reader, err := zip.OpenReader(archive)
	if err != nil {
		return "", err
	}
	defer reader.Close()
	found := map[string]bool{}
	for _, file := range reader.File {
		if !file.FileInfo().IsDir() {
			found[file.Name] = true
		}
	}
	for _, name := range winUIRequiredFiles {
		if !found[name] {
			return "", fmt.Errorf("WinUI 更新包缺少 %s", name)
		}
	}
	// Stale runtime assemblies must never survive staging from an older package.
	if !filepath.IsAbs(destination) || filepath.Base(destination) != "prepared" {
		return "", errors.New("无效更新暂存路径")
	}
	if err = os.RemoveAll(destination); err != nil {
		return "", err
	}
	if err = Extract(archive, destination); err != nil {
		return "", err
	}
	if err = validateWinUILayout(destination); err != nil {
		return "", err
	}
	return destination, nil
}
func validateWinUILayout(directory string) error {
	for _, name := range winUIRequiredFiles {
		info, err := os.Lstat(filepath.Join(directory, name))
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() || info.Size() == 0 {
			return fmt.Errorf("无效 WinUI 文件 %s", name)
		}
	}
	return nil
}
